"""Tooling tests for Tools/PerfSampler.py and the MemorySampler it wraps.

The contract: external once-per-second process-memory samples that name
their metric/source/units, with failures recorded as `unavailable` —
never a fabricated zero and never Unity heap data.
"""

import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

from AgentScenario import MemorySampler, _android_total_pss_bytes
import PerfSampler


class MemorySamplerTests(unittest.TestCase):
    def _records(self, directory):
        path = Path(directory) / "memory.ndjson"
        assert path.is_file(), "sampler wrote no memory.ndjson"
        return [json.loads(line) for line in
                path.read_text(encoding="utf-8").splitlines()
                if line.strip()]

    @unittest.skipUnless(sys.platform == "win32",
                         "working-set sampling is a Windows path")
    def test_windows_pid_sample_names_metric_source_units(self):
        with tempfile.TemporaryDirectory() as directory:
            sampler = MemorySampler(directory, pid=os.getpid(),
                                    interval=0.2)
            sampler.start()
            time.sleep(0.7)
            sampler.stop()
            records = self._records(directory)
            assert records, "no samples recorded"
            ok = [r for r in records if r["status"] == "ok"]
            assert ok, "self pid must sample successfully"
            record = ok[0]
            assert record["metric"] == "process.workingSet"
            assert record["source"] == "psapi GetProcessMemoryInfo"
            assert record["units"] == "bytes"
            assert isinstance(record["value"], int) and record["value"] > 0

    @unittest.skipUnless(sys.platform == "win32",
                         "working-set sampling is a Windows path")
    def test_failed_sample_is_unavailable_not_zero(self):
        with tempfile.TemporaryDirectory() as directory:
            # A bogus pid must produce an honest unavailable record —
            # the failure can never masquerade as a measurement.
            sampler = MemorySampler(directory, pid=0x7FFFFFFE,
                                    interval=0.2)
            sampler.start()
            time.sleep(0.5)
            sampler.stop()
            records = self._records(directory)
            assert records, "no samples recorded"
            record = records[0]
            assert record["status"] == "unavailable", record
            assert "value" not in record, "failure must not fabricate a value"
            assert record["metric"] == "process.workingSet"
            assert record["units"] == "bytes"
            assert record["reason"]

    @unittest.skipUnless(sys.platform == "win32",
                         "working-set sampling is a Windows path")
    def test_dead_pid_records_unavailable_not_zero_ok(self):
        # The M4 review finding: a killed process whose handle lingers
        # still resolves through OpenProcess, and the old code recorded
        # its ~0-byte working set as status:"ok". It must be
        # unavailable with a reason, carrying no fabricated value.
        # Keep the Popen object alive through sampling: its held
        # process handle keeps the dead pid resolvable, reproducing
        # the exact "terminated handle still samples" failure mode.
        process = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(30)"])
        process.kill()
        process.wait()
        with tempfile.TemporaryDirectory() as directory:
            sampler = MemorySampler(directory, pid=process.pid,
                                    interval=0.2)
            sampler.start()
            time.sleep(0.5)
            sampler.stop()
            records = self._records(directory)
        assert records, "no samples recorded"
        for record in records:
            assert record["status"] == "unavailable", record
            assert "value" not in record, \
                "dead pid must not fabricate a measurement"
            assert record["reason"]

    def test_pid_reuse_raises_instead_of_sampling_new_process(self):
        # A relaunched player can reuse the pid number; a changed
        # creation stamp must surface as a failure (unavailable
        # record), never an "ok" on a different process.
        with tempfile.TemporaryDirectory() as directory:
            sampler = MemorySampler(directory, pid=12345, interval=0.1)
            with mock.patch("AgentScenario._windows_process_sample",
                            side_effect=[(4096, 111), (4096, 222)]):
                sampler._sample()
                with self.assertRaises(OSError):
                    sampler._sample()

    def test_stop_on_never_started_sampler_does_not_raise(self):
        with tempfile.TemporaryDirectory() as directory:
            MemorySampler(directory, pid=os.getpid()).stop()

    def test_sampler_requires_a_target(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(AssertionError):
                MemorySampler(directory)


class AndroidPssParserTests(unittest.TestCase):
    def test_total_pss_row_parses_to_bytes(self):
        output = ("Applications Memory Usage (in Kilobytes):\n"
                  "Total PSS:      123,456 kB\n")
        completed = mock.Mock(returncode=0, stdout=output, stderr="")
        with mock.patch("subprocess.run", return_value=completed) as run:
            value = _android_total_pss_bytes("SERIAL", "com.x.y")
        assert value == 123456 * 1024
        assert run.call_args[0][0][:3] == ["adb", "-s", "SERIAL"]

    def test_missing_pss_row_raises(self):
        completed = mock.Mock(returncode=0, stdout="no memory here",
                              stderr="")
        with mock.patch("subprocess.run", return_value=completed):
            with self.assertRaises(OSError):
                _android_total_pss_bytes("SERIAL", "com.x.y")


class PerfSamplerCliTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "win32",
                         "working-set sampling is a Windows path")
    def test_main_writes_bounded_ndjson_and_result(self):
        with tempfile.TemporaryDirectory() as directory:
            argv = ["PerfSampler.py", "--pid", str(os.getpid()),
                    "--evidence", str(Path(directory) / "run"),
                    "--interval", "0.2", "--seconds", "0.7"]
            with mock.patch.object(sys, "argv", argv):
                assert PerfSampler.main() == 0
            path = Path(directory) / "run" / "memory.ndjson"
            assert path.is_file(), "sampler wrote no memory.ndjson"
            records = [json.loads(line) for line in
                       path.read_text(encoding="utf-8").splitlines()
                       if line.strip()]
            assert records, "no samples recorded"
            result = json.loads(
                (Path(directory) / "run" / "result.json")
                .read_text(encoding="utf-8"))
            assert result["status"] == "passed"
            assert result["metric"] == "process.workingSet"
            assert result["okSamples"] >= 1

    @unittest.skipUnless(sys.platform == "win32",
                         "working-set sampling is a Windows path")
    def test_main_never_passes_with_zero_ok_samples(self):
        # A run on a dead pid must report a failing status — a
        # status-only consumer can never see "passed" for a run
        # that measured nothing.
        process = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(30)"])
        process.kill()
        process.wait()
        with tempfile.TemporaryDirectory() as directory:
            argv = ["PerfSampler.py", "--pid", str(process.pid),
                    "--evidence", str(Path(directory) / "run"),
                    "--interval", "0.2", "--seconds", "0.5"]
            with mock.patch.object(sys, "argv", argv):
                assert PerfSampler.main() == 1
            result = json.loads(
                (Path(directory) / "run" / "result.json")
                .read_text(encoding="utf-8"))
            assert result["status"] != "passed", result
            assert result["status"] == "failed"
            assert result["okSamples"] == 0
            assert result["samples"] >= 1
            assert result["unavailableSamples"] == result["samples"]

    @unittest.skipUnless(sys.platform == "win32",
                         "working-set sampling is a Windows path")
    def test_interrupt_during_bounded_run_still_writes_result(self):
        with tempfile.TemporaryDirectory() as directory:
            argv = ["PerfSampler.py", "--pid", str(os.getpid()),
                    "--evidence", str(Path(directory) / "run"),
                    "--interval", "0.2", "--seconds", "30"]
            sleeps = mock.patch.object(
                PerfSampler.time, "sleep",
                side_effect=KeyboardInterrupt)
            with mock.patch.object(sys, "argv", argv), sleeps:
                PerfSampler.main()
            result_path = Path(directory) / "run" / "result.json"
            assert result_path.is_file(), \
                "interrupt must not lose result.json"
            result = json.loads(
                result_path.read_text(encoding="utf-8"))
            assert result["interrupted"] is True
            assert "samples" in result


if __name__ == "__main__":
    unittest.main()
