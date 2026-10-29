import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from devicescope.specifications_platform import firmware_section, parse_edid, parse_xrandr, display_section, platform_sections
from devicescope.system import System, CommandResult


class PlatformSpecificationsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.system = System(self.temp.name, commands=False)

    def put(self, name, value):
        path = self.system.path(name)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value if isinstance(value, bytes) else value.encode())
        return path

    def values(self, section):
        return {row["label"]: row["value"] for row in section["rows"]}

    def secure_variable(self, value):
        return self.put('/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c', value)

    def test_secureboot_reads_data_after_attributes_and_preserves_disabled(self):
        for value, expected in [(b'\x07\x00\x00\x00\x01', 'Enabled'), (b'\x07\x00\x00\x00\x00', 'Disabled')]:
            with self.subTest(expected=expected):
                self.secure_variable(value)
                rows = self.values(firmware_section(self.system)[0])
                self.assertEqual(expected, rows['Secure Boot'])
                self.assertEqual('UEFI (kernel EFI interface)', rows['Boot firmware mode'])

    def test_malformed_and_missing_secureboot_are_not_disabled(self):
        for value in (b'\x01', b'\x07\x00\x00\x00\x02', b'\x07\x00\x00\x00\x01\x00'):
            with self.subTest(value=value):
                self.secure_variable(value)
                self.assertTrue(self.values(firmware_section(self.system)[0])['Secure Boot'].startswith('Unavailable'))
        self.secure_variable(b'').unlink()
        self.assertTrue(self.values(firmware_section(self.system)[0])['Secure Boot'].startswith('Unavailable'))

    def test_secureboot_permission_error_is_unavailable(self):
        self.secure_variable(b'\x07\x00\x00\x00\x01')
        with patch('devicescope.specifications_platform._binary', return_value=None):
            rows = self.values(firmware_section(self.system)[0])
        self.assertTrue(rows['Secure Boot'].startswith('Unavailable'))

    def test_no_efi_wsl_does_not_claim_bios_or_secureboot_disabled(self):
        self.put('/proc/sys/kernel/osrelease', '6.6.0-microsoft-standard-WSL2')
        section, notes = firmware_section(self.system)
        rows = self.values(section)
        self.assertTrue(rows['Boot firmware mode'].startswith('Unavailable'))
        self.assertTrue(rows['Secure Boot'].startswith('Unavailable'))
        self.assertIn("Windows host", ' '.join(notes))

    def test_firmware_oem_placeholders_not_presented_as_models(self):
        self.put('/sys/class/dmi/id/bios_vendor', 'Default string')
        self.put('/sys/class/dmi/id/bios_version', 'RealFirmware 1.2')
        self.put('/sys/class/dmi/id/bios_date', '10/20/2026')
        rows = self.values(firmware_section(self.system)[0])
        self.assertEqual('Unavailable', rows['Manufacturer'])
        self.assertEqual('RealFirmware 1.2', rows['Version'])

    def test_xrandr_multiple_monitors_current_rates_and_supported_modes(self):
        text = '''Screen 0: minimum 8 x 8, current 4480 x 1440, maximum 32767 x 32767
DP-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis)
   2560x1440     165.00*+ 144.00 120.00
   1920x1080      60.00
HDMI-1 connected 1920x1080+2560+0 (normal left inverted right x axis y axis)
   1920x1080     60.00*+ 59.94
DP-2 disconnected (normal left inverted right x axis y axis)
'''
        displays = parse_xrandr(text)
        self.assertEqual(2, len(displays))
        self.assertEqual('2560x1440', displays[0]['resolution'])
        self.assertEqual('165', displays[0]['refresh_hz'])
        self.assertEqual('60', displays[1]['refresh_hz'])
        self.assertIn('1920x1080 @ 60, 59.94 Hz', displays[1]['modes'])

    def test_missing_refresh_is_not_inferred_from_geometry_or_preferred_mode(self):
        outputs = parse_xrandr('DP-1 connected 1920x1080+0+0\n   1920x1080 60.00+\n')
        self.assertIsNone(outputs[0]['refresh_hz'])

    def test_rotated_output_keeps_geometry_and_current_mode_refresh(self):
        outputs = parse_xrandr('DP-1 connected 1080x1920+0+0 left\n   1920x1080 60.00*+\n')
        self.assertEqual('1080x1920', outputs[0]['resolution'])
        self.assertEqual('60', outputs[0]['refresh_hz'])

    def test_interlaced_mode_keeps_marker_and_reported_refresh(self):
        outputs = parse_xrandr('HDMI-1 connected 1920x1080+0+0\n   1920x1080i 60.00*+ 59.94\n')
        self.assertEqual('1920x1080', outputs[0]['resolution'])
        self.assertEqual('60', outputs[0]['refresh_hz'])
        self.assertEqual(['1920x1080i @ 60, 59.94 Hz'], outputs[0]['modes'])

    def edid(self):
        data = bytearray(128)
        data[:8] = b'\x00\xff\xff\xff\xff\xff\xff\x00'
        data[8:10] = ((4 << 10) | (5 << 5) | 12).to_bytes(2, 'big') # DEL
        data[54:72] = b'\x00\x00\x00\xfc\x00FixturePanel\n'
        data[127] = -sum(data) % 256
        self.assertEqual(128, len(data))
        return bytes(data)

    def test_edid_validated_before_using_model_and_manufacturer_id(self):
        self.assertEqual({'manufacturer': 'DEL', 'model': 'FixturePanel'}, parse_edid(self.edid()))
        bad = bytearray(self.edid()); bad[20] ^= 1
        for data in (None, b'abc', bytes(bad)):
            self.assertEqual('Unavailable', parse_edid(data)['model'])

    def test_xwayland_data_is_labelled_virtual_not_assigned_to_physical_connector(self):
        self.put('/sys/class/drm/card0-DP-1/status', 'connected')
        self.put('/sys/class/drm/card0-DP-1/modes', '3840x2160\n1920x1080\n')
        self.put('/sys/class/drm/card0-DP-1/edid', self.edid())
        result = CommandResult(0, stdout='XWAYLAND0 connected 1920x1080+0+0\n   1920x1080 59.96*+\n')
        with patch.object(self.system, 'run', return_value=result) as command:
            section, notes = display_section(self.system)
        rows = self.values(section)
        command.assert_called_once_with(['xrandr', '--query'], timeout=3)
        self.assertEqual('Unavailable', rows['Connector 1 current refresh rate'])
        self.assertEqual('59.96 Hz', rows['Desktop output 1 current refresh rate'])
        self.assertIn('virtual', rows['Desktop output 1'])
        self.assertIn('not the physical monitor', ' '.join(notes))
        self.assertEqual('3840x2160; 1920x1080', rows['Connector 1 supported resolutions (kernel)'])

    def test_exact_unique_connector_name_can_share_current_mode(self):
        self.put('/sys/class/drm/card0-DP-1/status', 'connected')
        result = CommandResult(0, stdout='DP-1 connected 2560x1440+0+0\n   2560x1440 144.00*+\n')
        with patch.object(self.system, 'run', return_value=result):
            section, _ = display_section(self.system)
        self.assertEqual('144 Hz', self.values(section)['Connector 1 current refresh rate'])
        self.put('/sys/class/drm/card1-DP-1/status', 'connected')
        with patch.object(self.system, 'run', return_value=result):
            section, _ = display_section(self.system)
        rows = self.values(section)
        self.assertEqual('Unavailable', rows['Connector 1 current refresh rate'])
        self.assertEqual('Unavailable', rows['Connector 2 current refresh rate'])

    def test_empty_environment_explicitly_unavailable_and_never_modified(self):
        before = list(Path(self.temp.name).rglob('*'))
        report = platform_sections(self.system)
        self.assertEqual(['Firmware', 'Display'], [s['title'] for s in report['sections']])
        rows = self.values(report['sections'][1])
        self.assertEqual('Unavailable', rows['Current refresh rate'])
        self.assertEqual(before, list(Path(self.temp.name).rglob('*')))


if __name__ == '__main__':
    unittest.main()
