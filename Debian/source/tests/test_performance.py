import tempfile
import unittest
from pathlib import Path

from devicescope.performance import PerformanceSampler, parse_cpu, parse_memory, parse_nvidia, read_sensors
from devicescope.system import System


class PerformanceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.system = System(self.tmp.name, commands=False)

    def put(self, name, value):
        target = self.system.path(name)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(value, encoding="utf-8")

    def test_guest_not_counted_twice(self):
        self.assertEqual(sum(parse_cpu("cpu 100 10 20 200 30 4 5 6 70 8\n")), 375)

    def test_cpu_two_samples_and_decrease_gap(self):
        self.put("/proc/stat", "cpu 100 0 100 100 0 0 0 0 99 0\n")
        sampler = PerformanceSampler(self.system)
        self.assertIsNone(sampler.sample()["cpu_percent"])
        self.put("/proc/stat", "cpu 110 0 110 180 0 0 0 0 109 0\n")
        self.assertAlmostEqual(sampler.sample()["cpu_percent"], 20)
        self.put("/proc/stat", "cpu 1 0 1 1 0 0 0 0\n")
        self.assertIsNone(sampler.sample()["cpu_percent"])

    def test_available_memory_not_free_memory(self):
        reading = parse_memory("MemTotal: 1000 kB\nMemFree: 100 kB\nMemAvailable: 600 kB\n")
        self.assertEqual(reading["ram_used"], 400 * 1024)
        self.assertAlmostEqual(reading["ram_percent"], 40)
        self.assertIsNone(parse_memory("MemTotal: 1000 kB\nMemFree: 100 kB\n")["ram_percent"])
        self.assertIsNone(parse_memory("MemTotal: 1000 kB\nMemAvailable: 2000 kB\n")["ram_percent"])

    def test_hwmon_identity_zero_and_faults(self):
        self.put("/sys/class/hwmon/hwmon2/name", "testchip")
        self.put("/sys/class/hwmon/hwmon2/temp1_input", "0")
        self.put("/sys/class/hwmon/hwmon2/temp1_label", "Board edge")
        self.put("/sys/class/hwmon/hwmon2/temp2_input", "72000")
        self.put("/sys/class/hwmon/hwmon2/temp2_fault", "1")
        self.put("/sys/class/hwmon/hwmon2/temp3_input", "1200")
        self.put("/sys/class/hwmon/hwmon2/temp3_type", "4")
        self.put("/sys/class/thermal/thermal_zone5/type", "acpitz")
        self.put("/sys/class/thermal/thermal_zone5/temp", "45000")
        sensors = read_sensors(self.system)
        self.assertEqual(len(sensors), 2)
        self.assertEqual(sensors[0]["name"], "testchip / Board edge")
        self.assertEqual(sensors[0]["celsius"], 0)
        self.assertEqual(sensors[1]["name"], "Thermal zone / acpitz")

    def test_nvidia_unavailable_is_not_zero(self):
        rows, sensors = parse_nvidia("GPU-111, GPU model, 00000000:01:00.0, [N/A], 12, 2048, 45\n")
        self.assertEqual(len(rows), 1)
        self.assertIsNone(rows[0]["utilization"])
        self.assertEqual(rows[0]["memory_used"], 12 * 1024**2)
        self.assertEqual(sensors[0]["celsius"], 45)
        self.assertEqual(parse_nvidia("error: no device"), ([], []))

    def test_amdgpu_kernel_reading(self):
        self.put("/sys/class/drm/card0/device/vendor", "0x1002")
        self.put("/sys/class/drm/card0/device/gpu_busy_percent", "67")
        self.put("/sys/class/drm/card0/device/mem_info_vram_used", "1048576")
        self.put("/sys/class/drm/card0/device/mem_info_vram_total", "8388608")
        sample = PerformanceSampler(self.system).sample()
        self.assertEqual(sample["gpus"][0]["utilization"], 67)
        self.assertEqual(sample["gpus"][0]["memory_used"], 1048576)

    def test_intel_no_supported_busy_counter_is_explicitly_unavailable(self):
        self.put("/sys/class/drm/card1/device/vendor", "0x8086")
        sample = PerformanceSampler(self.system).sample()
        self.assertEqual(len(sample["gpus"]), 1)
        self.assertIsNone(sample["gpus"][0]["utilization"])
        self.assertTrue(any("Intel GPU load is unavailable" in note for note in sample["notes"]))


if __name__ == "__main__":
    unittest.main()
