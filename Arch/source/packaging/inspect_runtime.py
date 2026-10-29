"""Inspect ELF metadata in a runtime archive without executing foreign binaries."""
from __future__ import annotations

import argparse
import json
import posixpath
from pathlib import PurePosixPath
import re
import struct
import tarfile


def _string(data, offset):
    end = data.find(b"\0", offset)
    if offset < 0 or offset >= len(data):
        return ""
    return data[offset:end if end >= 0 else None].decode("utf-8", "replace")


def inspect_elf(data):
    if not data.startswith(b"\x7fELF"):
        return None
    if len(data) < 64 or data[4] != 2 or data[5] not in {1, 2}:
        raise ValueError("Only ELF64 little/big-endian objects are supported")
    order = "<" if data[5] == 1 else ">"
    header = struct.unpack_from(order + "HHIQQQIHHHHHH", data, 16)
    machine, section_offset, section_size, section_count = header[1], header[5], header[10], header[11]
    if section_size < 64 or section_offset + section_size * section_count > len(data):
        raise ValueError("Invalid ELF section table")
    sections = [struct.unpack_from(order + "IIQQQQIIQQ", data, section_offset + section_size * i)
                for i in range(section_count)]
    result = {"machine": machine, "interpreter": "", "needed": [], "soname": "", "rpath": "", "runpath": "", "versions": [],
              "defined_gui_symbols": [], "undefined_gui_symbols": [],
              "optional_library_strings": sorted(set(value.decode("ascii") for value in re.findall(
                  rb"lib(?:X11|Xft|Xrender|Xext|Xcursor|fontconfig|freetype|xcb)[-A-Za-z0-9_.]*\.so(?:\.[0-9]+)*", data)))}
    program_offset, program_size, program_count = header[4], header[8], header[9]
    if program_count and (program_size < 56 or program_offset + program_size * program_count > len(data)):
        raise ValueError("Invalid ELF program table")
    for index in range(program_count):
        program = struct.unpack_from(order + "IIQQQQQQ", data, program_offset + program_size * index)
        if program[0] == 3:
            result["interpreter"] = _string(data, program[2])

    def contents(section):
        offset, size = section[4], section[5]
        if offset + size > len(data):
            raise ValueError("ELF section lies beyond the file")
        return data[offset:offset + size]

    for section in sections:
        if section[1] not in {2, 6, 11, 0x6FFFFFFE}:
            continue
        linked = section[6]
        if linked >= len(sections):
            raise ValueError("Invalid ELF linked string section")
        strings = contents(sections[linked])
        body = contents(section)
        if section[1] == 6:
            for offset in range(0, len(body) - 15, 16):
                tag, value = struct.unpack_from(order + "qQ", body, offset)
                if tag == 0:
                    break
                if tag == 1:
                    result["needed"].append(_string(strings, value))
                elif tag in {14, 15, 29}:
                    result[{14: "soname", 15: "rpath", 29: "runpath"}[tag]] = _string(strings, value)
        elif section[1] in {2, 11}:
            size = section[9] or 24
            for offset in range(0, len(body) - 23, size):
                name_offset, _, _, section_index, _, _ = struct.unpack_from(order + "IBBHQQ", body, offset)
                name = _string(strings, name_offset)
                if name in {"XOpenDisplay", "XCloseDisplay", "XftFontOpen", "FcInit", "FT_Init_FreeType", "xcb_connect"}:
                    bucket = result["defined_gui_symbols" if section_index else "undefined_gui_symbols"]
                    if name not in bucket:
                        bucket.append(name)
        else:
            offset, seen = 0, set()
            while offset + 16 <= len(body) and offset not in seen:
                seen.add(offset)
                _, count, library_offset, aux_offset, next_offset = struct.unpack_from(order + "HHIII", body, offset)
                library = _string(strings, library_offset)
                cursor, seen_aux = offset + aux_offset, set()
                for _ in range(count):
                    if cursor + 16 > len(body) or cursor in seen_aux:
                        break
                    seen_aux.add(cursor)
                    _, _, _, name_offset, aux_next = struct.unpack_from(order + "IHHII", body, cursor)
                    result["versions"].append({"library": library, "version": _string(strings, name_offset)})
                    if not aux_next:
                        break
                    cursor += aux_next
                if not next_offset:
                    break
                offset += next_offset
    return result


def version_key(text):
    return tuple(int(value) for value in text.split("_", 1)[-1].split(".") if value.isdigit())


def inspect_archive(path):
    objects, all_files = {}, []
    with tarfile.open(path, "r:gz") as archive:
        for member in archive:
            if member.isfile() or member.issym() or member.islnk():
                all_files.append(member.name)
            if not member.isfile() or member.size < 64:
                continue
            stream = archive.extractfile(member)
            if stream is None:
                continue
            prefix = stream.read(4)
            if prefix != b"\x7fELF":
                continue
            info = inspect_elf(prefix + stream.read())
            if info is not None:
                objects[member.name] = info
    bundled_names = {PurePosixPath(name).name for name in all_files}
    bundled_names.update(obj["soname"] for obj in objects.values() if obj["soname"])
    def bundled(owner, needed):
        if "/" in needed:
            path = needed.replace("${ORIGIN}", posixpath.dirname(owner)).replace("$ORIGIN", posixpath.dirname(owner))
            return posixpath.normpath(path) in all_files
        return needed in bundled_names

    externals = sorted({needed for owner, obj in objects.items() for needed in obj["needed"] if not bundled(owner, needed)})
    glibc = sorted({version["version"] for obj in objects.values() for version in obj["versions"]
                    if re.fullmatch(r"GLIBC_\d+(?:\.\d+)+", version["version"])}, key=version_key)
    requiring_max = [name for name, obj in objects.items()
                     if glibc and any(value["version"] == glibc[-1] for value in obj["versions"])]
    return {"archive": str(path), "elf_count": len(objects),
            "elf_machines": sorted({obj["machine"] for obj in objects.values()}),
            "elf_interpreters": sorted({obj["interpreter"] for obj in objects.values() if obj["interpreter"]}),
            "required_glibc_max": glibc[-1] if glibc else None,
            "requires_max_glibc": requiring_max, "external_shared_libraries": externals, "objects": objects,
            "note": "Static ELF DT_NEEDED/SONAME/symbol-version inspection only. dlopen dependencies, X server access, fonts/configuration and actual execution still require runtime testing. Bundled names do not prove runtime loader reachability."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive")
    parser.add_argument("--output")
    args = parser.parse_args()
    report = inspect_archive(args.archive)
    if args.output:
        with open(args.output, "w", encoding="utf-8") as target:
            json.dump(report, target, indent=2)
    print(json.dumps({key: value for key, value in report.items() if key != "objects"}, indent=2))
    print("\nPython / Tcl / Tk loader metadata:")
    for name, obj in report["objects"].items():
        basename = PurePosixPath(name).name
        if basename.startswith(("python3.", "libpython", "libtcl", "libtk", "_tkinter")):
            print(name, json.dumps({key: obj[key] for key in ("needed", "rpath", "runpath", "defined_gui_symbols", "undefined_gui_symbols", "optional_library_strings")}))


if __name__ == "__main__":
    main()
