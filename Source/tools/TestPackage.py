"""Test package publication/failure recovery against an already audited build."""
import argparse
import contextlib
import io
import hashlib
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

import Package


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, required=True)
    parser.add_argument("--log", type=Path, help="Save qualified results and exact tooling hashes")
    args = parser.parse_args()
    checks = 0
    with tempfile.TemporaryDirectory(prefix="manual-roll-package-test-") as directory:
        folder = Path(directory)
        readme = folder / "README.md"
        results = folder / "TestResults.md"
        readme.write_text("Packaging regression fixture only.\n", encoding="utf-8")
        results.write_text("No game runtime or gameplay claims.\n", encoding="utf-8")
        output = folder / "package.zip"
        arguments = ["Package.py", "--build", str(args.build.resolve()), "--output", str(output),
                     "--readme", str(readme), "--test-results", str(results)]

        def publish():
            with patch.object(sys, "argv", arguments), contextlib.redirect_stdout(io.StringIO()):
                Package.main()

        publish()
        expected = output.read_bytes()
        assert expected[:2] == b"PK"
        checks += 1
        with Package.ZipFile(output) as archive:
            assert archive.testzip() is None
            checks += 1
            binaries = [name for name in archive.namelist() if name.lower().endswith((".dll", ".exe"))]
            assert binaries == ["ManualDiceRoller/ManualDiceRoller.dll"]
            checks += 1
            assert archive.read(binaries[0]) == (args.build / "ManualDiceRoller/ManualDiceRoller.dll").read_bytes()
            checks += 1
            assert all("\\" not in name and not name.startswith("/") and ".." not in Path(name).parts for name in archive.namelist())
            checks += 1
            assert all("__pycache__" not in name and not name.endswith(".pyc") for name in archive.namelist())
            checks += 1

        original_read = Package.ZipFile.read

        def corrupt_read(archive, name, *positional, **keyword):
            if name == "ManualDiceRoller/ManualDiceRoller.dll":
                return b"deliberately corrupted verification read"
            return original_read(archive, name, *positional, **keyword)

        cases = [
            ("write failure", patch.object(Package.ZipFile, "writestr", side_effect=OSError("injected write failure")), OSError),
            ("CRC failure", patch.object(Package.ZipFile, "testzip", return_value="deliberately-bad-entry"), RuntimeError),
            ("checksum failure", patch.object(Package.ZipFile, "read", new=corrupt_read), RuntimeError),
            ("publish sharing failure", patch.object(Package.os, "replace", side_effect=PermissionError("injected sharing violation")), PermissionError),
        ]
        for name, injection, expected_error in cases:
            with injection:
                try:
                    publish()
                except expected_error:
                    checks += 1
                else:
                    raise AssertionError(name + " was not detected")
            assert output.read_bytes() == expected, name + " damaged existing package"
            checks += 1
            assert not list(folder.glob("package.zip.*.tmp")), name + " left a temporary file"
            checks += 1
        # Missing live evidence must fail before any candidate publication.
        production = arguments + ["--qualification", "production"]
        with patch.object(sys, "argv", production), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            try:
                Package.main()
            except SystemExit as failure:
                assert failure.code == 2
                checks += 1
            else:
                raise AssertionError("Production package without acceptance was permitted")
        assert output.read_bytes() == expected
        checks += 1
        assert not list(folder.glob("package.zip.*.tmp"))
        checks += 1
        publish()
        assert output.read_bytes() == expected, "identical inputs should publish identical bytes"
        checks += 1
    report = "PASS " + str(checks) + " package checks: real ZIP/sole runtime binary/input parity, Windows names, cache exclusion, write/CRC/checksum/sharing-failure recovery, production-evidence rejection, deterministic repackage.\n"
    report += "This tests publication of an already audited plugin; no Unity or game execution occurs.\n"
    for name in ("Build.py", "Package.py", "TestPackage.py"):
        report += name + " SHA256 " + hashlib.sha256((Path(__file__).resolve().parent / name).read_bytes()).hexdigest() + "\n"
    if args.log:
        args.log.parent.mkdir(parents=True, exist_ok=True)
        args.log.write_text(report, encoding="utf-8")
    print(report, end="")


if __name__ == "__main__":
    main()
