"""Executable negative tests for evidence gates and environment resolution.

Synthetic files exercise verifier decisions only. No result from this suite is
actual Windows, Mono, Unity or game acceptance evidence.
"""
import json
import contextlib
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from RunRegression import CORE_SUITES, source_hashes
from FixtureEvidence import FIXTURE_MANIFESTS, inspect as inspect_fixture_evidence
from TestEnvironment import chosen, version_key, sha256
from ValidateRelease import validate


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value))


class EnvironmentTests(unittest.TestCase):
    def test_numeric_version_order(self):
        self.assertGreater(version_key("8.0.31"), version_key("8.0.9"))

    def test_explicit_argument_wins(self):
        with patch.dict(os.environ, {"MANUALROLL_TEST_MANAGED": "/env value"}):
            self.assertEqual(chosen("/argument value", "MANUALROLL_TEST_MANAGED"), Path("/argument value").resolve())

    def test_environment_wins_over_default(self):
        with patch.dict(os.environ, {"MANUALROLL_TEST_MANAGED": "/env value"}):
            self.assertEqual(chosen(None, "MANUALROLL_TEST_MANAGED", "/fallback"), Path("/env value").resolve())


class GateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="manual roll evidence fixture ")
        self.root = Path(self.temporary.name)
        self.build = self.root / "build"
        self.report = self.root / "reports/regression-report.json"
        self.windows = self.root / "reports/windows-acceptance.json"
        self.metadata = {"Id":"ManualDiceRoller", "DisplayName":"Manual Dice Roller", "Version":"0.8.4.2", "ManagerVersion":"0.32.4", "AssemblyName":"ManualDiceRoller.dll", "EntryMethod":"ManualDiceRoller.Plugin.Load"}
        write_json(self.root / "src/info.template.json", self.metadata)
        (self.root / "src/Plugin.cs").write_text(
            "// verifier fixture, not a runtime plugin\nnamespace ManualDiceRoller\n{\n"
            "    public static class Plugin {}\n    // These guards leave Unity fixture boundary\n"
            "    internal static class InputGuards {}\n}\n")
        (self.root / "tools").mkdir()
        (self.root / "tools/tool.py").write_text("# verifier fixture")
        definition = json.loads((Path(__file__).parent / "acceptance-cases.json").read_text())
        write_json(self.root / "tools/acceptance-cases.json", definition)
        self.mp_definition = json.loads((Path(__file__).parent / "multiplayer-acceptance-cases.json").read_text())
        write_json(self.root / "tools/multiplayer-acceptance-cases.json", self.mp_definition)
        for name in CORE_SUITES:
            (self.root / "tests" / name).mkdir(parents=True, exist_ok=True)
            (self.root / "tests" / name / "run.py").write_text("# verifier fixture")
        for name in FIXTURE_MANIFESTS:
            manifest = self.root / name
            result = manifest.parent / "results.txt"
            result.write_text("Synthetic fixture result, not game execution.\n")
            write_json(manifest, {"status":"passed", "sources":{"src/Plugin.cs":sha256(self.root / "src/Plugin.cs")},
                                  "results_sha256":sha256(result)})
        (self.build / "ManualDiceRoller").mkdir(parents=True)
        plugin = self.build / "ManualDiceRoller/ManualDiceRoller.dll"
        plugin.write_bytes(b"synthetic verifier artifact, never load this")
        info = self.build / "ManualDiceRoller/Info.json"
        write_json(info, self.metadata)
        for name in ("assembly-audit.txt", "harmony-bindings.txt", "network-abi.txt", "dependencies.txt", "harmony-generator-probe.txt",
                     "harmony-bindings-negative-name.txt", "harmony-bindings-negative-type.txt"):
            (self.build / name).write_text("Synthetic audit fixture: 0 errors.")
        (self.build / "harmony-native-read.txt").write_text("Synthetic read fixture; 0 errors.")
        for kind in ("name", "type"):
            (self.build / ("harmony-bindings-negative-" + kind + ".txt")).write_text(
                "Synthetic audit fixture: 0 errors.\nNegative audit self-test --negative-" + kind + ": PASS\n")
        from TestEnvironment import GAME_SHA256, NETWORK_SHA256
        self.manifest = {"game_sha256":GAME_SHA256,"plugin_sha256":sha256(plugin), "info_sha256":sha256(info), "metadata":self.metadata,
                         "umm_sha256":"fixture-umm", "harmony_sha256":"fixture-harmony",
                         "sources":{"Plugin.cs":sha256(self.root / "src/Plugin.cs")},
                         "tools":{p.name:sha256(p) for p in (self.root / "tools").iterdir()},
                         "managed_references":{"Assembly-CSharp.dll":"fixture-reference",**NETWORK_SHA256}}
        self.manifest["audits"] = {p.name:sha256(p) for p in self.build.glob("*.txt")}
        write_json(self.build / "build-manifest.json", self.manifest)
        self.report.parent.mkdir(exist_ok=True)
        log = self.report.parent / "suite.txt"
        log.write_text("Synthetic verifier success fixture")
        operation = {"status":"pass", "log":log.name, "log_sha256":sha256(log)}
        sources = source_hashes(self.root)
        self.regression = {"status":"pass", "full_inventory_run":True, "suite_inventory":sorted(CORE_SUITES), "selected_suites":sorted(CORE_SUITES),
                           "sources_before":sources,"sources_after":sources,"managed_inputs_unchanged":True,"environment_inputs_unchanged":True,
                           "managed_inputs_before":self.manifest["managed_references"],"managed_inputs_after":self.manifest["managed_references"],
                           "fixture_evidence":inspect_fixture_evidence(self.root,self.manifest["managed_references"]),
                           "fixture_evidence_unchanged":True,
                           "suites":{name:operation.copy() for name in CORE_SUITES},"tool_regressions":operation.copy(),"build":operation.copy(),"package_regressions":operation.copy(),"powershell_grammar":operation.copy()}
        write_json(self.report, self.regression)
        from TestEnvironment import GAME_SHA256
        marker = {"utc":"2026-10-06T12:01:00+00:00", "version":"0.8.4.2", "plugin_sha256":self.manifest["plugin_sha256"],
                  "umm_version":"0.32.4.0","umm_sha256":"fixture-umm", "harmony_version":"2.3.6.0","harmony_sha256":"fixture-harmony",
                  "mono_runtime":True,"pointer_size":8,"unity_version":"fixture-unity","clr_version":"fixture-clr"}
        marker_log = self.report.parent / "runtime-log.txt"
        marker_log.write_text("MDR_DIAGNOSTICS " + json.dumps(marker) + "\n")
        self.diagnostics = {"schema_version":1,"platform":"windows","collected_utc":"2026-10-06T12:02:00+00:00","setup_errors":[], "game_path":"C:\\fixturegame",
                            "files":{key:{"sha256":value} for key,value in
                                     (("game",GAME_SHA256),("plugin",self.manifest["plugin_sha256"]),("info",self.manifest["info_sha256"]),("umm","fixture-umm"),("harmony","fixture-harmony"))},
                            "processes":[{"start_utc":"2026-10-06T12:00:00+00:00","executable_path":"C:\\fixturegame\\Solasta.exe"}],
                            "logs":[{"relative_path":"runtime-log.txt","sha256":sha256(marker_log)}],"runtime_markers":[{"value":marker}]}
        self.diagnostics["files"].update({key:{"sha256":NETWORK_SHA256[name]} for key,name in (("photon_pun","PhotonUnityNetworking.dll"),("photon_realtime","PhotonRealtime.dll"),("photon_client","Photon3Unity3D.dll"))})
        write_json(self.report.parent / "windows-diagnostics.json", self.diagnostics)
        evidence = self.report.parent / "observation.txt"
        evidence.write_text("Synthetic verifier observation. Never actual game evidence.")
        self.acceptance = {"schema_version":1,"profile":definition["profile"],"tester":"synthetic fixture", "session_started_utc":"2026-10-06T12:00:00+00:00",
                           "plugin_sha256":self.manifest["plugin_sha256"],"game_sha256":GAME_SHA256,"mod_version":"0.8.4.2","diagnostic_report":"windows-diagnostics.json",
                           "cases":[{"id":case["id"],"status":"pass","observed":"synthetic fixture observation","evidence":["observation.txt"]} for case in definition["cases"]]}
        write_json(self.windows,self.acceptance)
        self.multiplayer = self.report.parent / "multiplayer-acceptance.json"
        self.mp = {"schema_version":1,"profile":self.mp_definition["profile"],"tester":"synthetic fixture",
                   "session_started_utc":"2026-10-06T12:00:00+00:00","session":"synthetic-session","protocol":self.mp_definition["protocol"],
                   "plugin_sha256":self.manifest["plugin_sha256"],"game_sha256":GAME_SHA256,"mod_version":"0.8.4.2",
                   "peers":[],"cases":[],"recorded_rolls":[]}
        for actor,role in ((1,"host"),(2,"client")):
            folder = self.report.parent / role
            folder.mkdir()
            peer_log = folder / "runtime-log.txt"
            ready = {"utc":"2026-10-06T12:01:10+00:00","event":"session_ready","protocol":self.mp_definition["protocol"],
                     "plugin_sha256":self.manifest["plugin_sha256"],"game_sha256":GAME_SHA256,"local_actor":actor,
                     "master_actor":1,"members":[1,2],"session":"synthetic-session","profile":"a"*64,"room_sha256":"b"*64}
            completed = [{"utc":"2026-10-06T12:01:20+00:00","event":"roll_completed","session":"synthetic-session",
                          "sequence":i,"signature":case,"action":case,"owner":1,"faces":[7]}
                         for i,case in enumerate(self.mp_definition["recorded_roll_cases"])]
            peer_log.write_text("MDR_DIAGNOSTICS " + json.dumps(marker) + "\nMDR_NETWORK " + json.dumps(ready) + "\n" +
                                "".join("MDR_NETWORK " + json.dumps(m) + "\n" for m in completed))
            peer_diag = json.loads(json.dumps(self.diagnostics))
            peer_diag["logs"] = [{"relative_path":"runtime-log.txt","sha256":sha256(peer_log)}]
            write_json(folder / "windows-diagnostics.json",peer_diag)
            peer_acceptance = json.loads(json.dumps(self.acceptance))
            peer_acceptance["cases"] = []
            write_json(folder / "windows-acceptance.json",peer_acceptance)
            self.mp["peers"].append({"name":role,"role":role,"actor":actor,"acceptance_report":role+"/windows-acceptance.json"})
        self.mp["cases"] = [{"id":case["id"],"peer_actors":[1,2],"status":"pass","observed":"synthetic policy observation",
                            "evidence":["observation.txt"]} for case in self.mp_definition["cases"]]
        self.mp["recorded_rolls"] = [{"case_id":case,"action":case,"sequence":i,"signature":case,"owner":1,
                                     "expected_native_rng_draws":1,"expected_native_callbacks":1,"baseline_evidence":["observation.txt"],
                                     "peers":[{"actor":actor,"faces":[7],"native_result":"synthetic native result",
                                               "native_rng_draws":1,"native_callbacks":1} for actor in (1,2)]}
                                    for i,case in enumerate(self.mp_definition["recorded_roll_cases"])]
        write_json(self.multiplayer,self.mp)

    def tearDown(self):
        self.temporary.cleanup()

    def check(self, level="candidate", windows=False, multiplayer=None):
        include_mp = windows if multiplayer is None else multiplayer
        return validate(self.root,self.build,self.report,level,self.windows if windows else None,
                        self.multiplayer if include_mp else None)

    def save_updated_marker(self):
        log = self.report.parent / "runtime-log.txt"
        log.write_text("MDR_DIAGNOSTICS " + json.dumps(self.diagnostics["runtime_markers"][0]["value"]) + "\n")
        self.diagnostics["logs"][0]["sha256"] = sha256(log)
        write_json(self.report.parent / "windows-diagnostics.json",self.diagnostics)

    def test_candidate_never_claims_live(self):
        report = self.check()
        self.assertEqual(report["status"],"pass")
        self.assertFalse(report["live_windows_mono_qualified"])

    def test_production_requires_actual_acceptance(self):
        self.assertEqual(self.check("production")["status"],"fail")

    def test_complete_synthetic_policy_fixture(self):
        self.assertEqual(self.check("production",True)["status"],"pass")

    def test_production_requires_actual_multiplayer(self):
        self.assertEqual(self.check("production",True,False)["status"],"fail")

    def test_candidate_never_claims_live_multiplayer(self):
        self.assertFalse(self.check()["live_multiplayer_qualified"])

    def test_missing_network_handshake_rejected(self):
        peer_log = self.report.parent / "client/runtime-log.txt"
        peer_log.write_text(peer_log.read_text().split("MDR_NETWORK ")[0])
        diagnostics_path = self.report.parent / "client/windows-diagnostics.json"
        diagnostics = json.loads(diagnostics_path.read_text())
        diagnostics["logs"][0]["sha256"] = sha256(peer_log)
        write_json(diagnostics_path,diagnostics)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def mutate_client_marker(self, change):
        peer_log = self.report.parent / "client/runtime-log.txt"
        lines = peer_log.read_text().splitlines()
        for i,line in enumerate(lines):
            if line.startswith("MDR_NETWORK "):
                value = json.loads(line.split("MDR_NETWORK ",1)[1])
                change(value)
                lines[i] = "MDR_NETWORK " + json.dumps(value)
        peer_log.write_text("\n".join(lines)+"\n")
        path = self.report.parent / "client/windows-diagnostics.json"
        diagnostics = json.loads(path.read_text())
        diagnostics["logs"][0]["sha256"] = sha256(peer_log)
        write_json(path,diagnostics)

    def test_future_network_marker_rejected(self):
        self.mutate_client_marker(lambda m: m.update(utc="2026-10-06T14:00:00+00:00"))
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_network_settings_profile_mismatch_rejected(self):
        self.mutate_client_marker(lambda m: m.update(profile="c"*64) if m["event"]=="session_ready" else None)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_network_room_mismatch_rejected(self):
        self.mutate_client_marker(lambda m: m.update(room_sha256="d"*64) if m["event"]=="session_ready" else None)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_completion_wrong_owner_rejected(self):
        self.mutate_client_marker(lambda m: m.update(owner=2) if m["event"]=="roll_completed" else None)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_completion_wrong_action_rejected(self):
        self.mutate_client_marker(lambda m: m.update(action="stale-action") if m["event"]=="roll_completed" else None)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_actual_transport_string_wire_fields_accepted(self):
        def as_strings(m):
            for name in ("protocol","local_actor","master_actor","sequence","owner"):
                if name in m: m[name]=str(m[name])
            for name in ("members","faces"):
                if name in m: m[name]=",".join(str(v) for v in m[name])
        self.mutate_client_marker(as_strings)
        self.assertEqual(self.check("production",True)["status"],"pass")

    def test_same_peer_actor_rejected(self):
        self.mp["peers"][1]["actor"] = 1
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_wrong_multiplayer_session_rejected(self):
        self.mp["session"] = "other-session"
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_multiplayer_wrong_protocol_rejected(self):
        self.mp["protocol"] = self.mp_definition["protocol"] + 1
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_old_protocol_readiness_rejected(self):
        self.mutate_client_marker(lambda m: m.update(protocol=1) if m["event"]=="session_ready" else None)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_multiplayer_wrong_faces_rejected(self):
        self.mp["recorded_rolls"][0]["peers"][1]["faces"] = [8]
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_multiplayer_rng_divergence_rejected(self):
        self.mp["recorded_rolls"][0]["peers"][1]["native_rng_draws"] = 0
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_multiplayer_callback_duplication_rejected(self):
        self.mp["recorded_rolls"][0]["peers"][1]["native_callbacks"] = 2
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_multiplayer_baseline_missing_rejected(self):
        self.mp["recorded_rolls"][0]["baseline_evidence"] = []
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_multiplayer_partial_case_rejected(self):
        self.mp["cases"][0]["peer_actors"] = [1]
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_duplicate_peer_report_rejected(self):
        self.mp["peers"][1]["acceptance_report"] = self.mp["peers"][0]["acceptance_report"]
        write_json(self.multiplayer,self.mp)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_photon_input_drift_rejected(self):
        self.manifest["managed_references"]["PhotonRealtime.dll"] = "other-game"
        write_json(self.build / "build-manifest.json",self.manifest)
        self.assertEqual(self.check()["status"],"fail")

    def test_changed_runtime_source_rejected(self):
        (self.root / "src/Plugin.cs").write_text("changed")
        self.assertEqual(self.check()["status"],"fail")

    def test_stale_fixture_hash_rejected_with_current_global_evidence(self):
        # Reproduce the release defect: build and complete-suite source hashes
        # describe current bytes, while a successful old fixture manifest
        # still identifies a previous production source.
        path = self.root / "tests/save/source-manifest.json"
        value = json.loads(path.read_text())
        value["sources"]["src/Plugin.cs"] = "0" * 64
        write_json(path,value)
        self.regression["fixture_evidence"]["manifest_sha256"]["tests/save/source-manifest.json"] = sha256(path)
        write_json(self.report,self.regression)
        result = self.check()
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("stale fixture input hash" in error for error in result["errors"]))
        self.assertEqual(self.regression["sources_after"], source_hashes(self.root))
        self.assertEqual(self.manifest["sources"]["Plugin.cs"],sha256(self.root / "src/Plugin.cs"))

    def test_removed_fixture_manifest_rejected(self):
        (self.root / "tests/save/source-manifest.json").unlink()
        result = self.check()
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("Required fixture manifest missing" in error for error in result["errors"]))

    def test_changed_fixture_manifest_after_run_rejected(self):
        path = self.root / "tests/network-protocol/source-manifest.json"
        value = json.loads(path.read_text())
        value["scope"] = "Changed after regression evidence was captured"
        write_json(path,value)
        self.assertEqual(self.check()["status"],"fail")

    def test_stale_fixture_result_rejected(self):
        (self.root / "tests/save/results.txt").write_text("Old result was replaced")
        result = self.check()
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("stale fixture input hash: results.txt" in error for error in result["errors"]))

    def test_stale_captured_plugin_source_rejected(self):
        path = self.root / "tests/plugin-lifecycle/source-manifest.json"
        value = json.loads(path.read_text())
        value["production_plugin_sha256"] = sha256(self.root / "src/Plugin.cs")
        value["captured_plugin_class_sha256"] = "0" * 64
        write_json(path,value)
        result = self.check()
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("stale captured source hash" in error for error in result["errors"]))

    def test_fixture_source_escape_rejected(self):
        path = self.root / "tests/save/source-manifest.json"
        value = json.loads(path.read_text())
        value["sources"]["src/../src/Plugin.cs"] = sha256(self.root / "src/Plugin.cs")
        write_json(path,value)
        result = self.check()
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("Noncanonical or escaping fixture input" in error for error in result["errors"]))

    def test_fixture_failed_status_rejected(self):
        path = self.root / "tests/save/source-manifest.json"
        value = json.loads(path.read_text())
        value["status"] = "failed"
        write_json(path,value)
        self.assertEqual(self.check()["status"],"fail")

    def test_missing_recorded_fixture_inventory_rejected(self):
        self.regression.pop("fixture_evidence")
        write_json(self.report,self.regression)
        self.assertEqual(self.check()["status"],"fail")

    def test_package_rejects_stale_fixture_and_preserves_previous_zip(self):
        import Package
        output = self.root / "candidate.zip"
        args = ["Package.py", "--build", str(self.build), "--output", str(output),
                "--readme", str(self.root / "tools/tool.py"), "--test-results", str(self.root / "tools/tool.py")]

        def publish():
            with patch.object(Package,"__file__",str(self.root / "tools/Package.py")), patch.object(sys,"argv",args), \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                Package.main()

        publish()
        before = output.read_bytes()
        path = self.root / "tests/save/source-manifest.json"
        value = json.loads(path.read_text())
        value["sources"]["src/Plugin.cs"] = "0" * 64
        write_json(path,value)
        with self.assertRaises(SystemExit) as error:
            publish()
        self.assertEqual(error.exception.code,2)
        self.assertEqual(output.read_bytes(),before)
        self.assertFalse(list(self.root.glob("candidate.zip.*.tmp")))

    def test_changed_tool_rejected(self):
        (self.root / "tools/tool.py").write_text("changed")
        self.assertEqual(self.check()["status"],"fail")

    def test_partial_regression_rejected(self):
        self.regression["full_inventory_run"] = False
        write_json(self.report,self.regression)
        self.assertEqual(self.check()["status"],"fail")

    def test_changed_log_rejected(self):
        (self.report.parent / "suite.txt").write_text("changed")
        self.assertEqual(self.check()["status"],"fail")

    def test_missing_suite_result_rejected(self):
        self.regression["suites"].pop(next(iter(self.regression["suites"])))
        write_json(self.report,self.regression)
        self.assertEqual(self.check()["status"],"fail")

    def test_missing_build_operation_rejected(self):
        self.regression.pop("build")
        write_json(self.report,self.regression)
        self.assertEqual(self.check()["status"],"fail")

    def test_missing_package_operation_rejected(self):
        self.regression.pop("package_regressions")
        write_json(self.report,self.regression)
        self.assertEqual(self.check()["status"],"fail")

    def test_missing_audit_evidence_rejected(self):
        self.manifest["audits"].pop("network-abi.txt")
        write_json(self.build / "build-manifest.json",self.manifest)
        self.assertEqual(self.check()["status"],"fail")

    def test_changed_artifact_rejected(self):
        (self.build / "ManualDiceRoller/ManualDiceRoller.dll").write_bytes(b"changed")
        self.assertEqual(self.check()["status"],"fail")

    def test_missing_marker_rejected(self):
        self.diagnostics["runtime_markers"] = []
        write_json(self.report.parent / "windows-diagnostics.json",self.diagnostics)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_marker_without_retained_log_rejected(self):
        self.diagnostics["logs"] = []
        write_json(self.report.parent / "windows-diagnostics.json",self.diagnostics)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_other_game_process_rejected(self):
        self.diagnostics["processes"][0]["executable_path"] = "C:\\othergame\\Solasta.exe"
        write_json(self.report.parent / "windows-diagnostics.json",self.diagnostics)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_coreclr_marker_rejected(self):
        self.diagnostics["runtime_markers"][0]["value"]["mono_runtime"] = False
        self.save_updated_marker()
        result = self.check("production",True)
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("Mono profile" in message for message in result["errors"]))

    def test_stale_process_marker_rejected(self):
        self.diagnostics["runtime_markers"][0]["value"]["utc"] = "2026-10-06T11:00:00+00:00"
        self.save_updated_marker()
        result = self.check("production",True)
        self.assertEqual(result["status"],"fail")
        self.assertTrue(any("current process session" in message for message in result["errors"]))

    def test_pending_case_rejected(self):
        self.acceptance["cases"][0]["status"] = "pending"
        write_json(self.windows,self.acceptance)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_empty_observation_rejected(self):
        self.acceptance["cases"][0]["observed"] = ""
        write_json(self.windows,self.acceptance)
        self.assertEqual(self.check("production",True)["status"],"fail")

    def test_evidence_path_escape_rejected(self):
        self.acceptance["cases"][0]["evidence"] = ["../outside.txt"]
        write_json(self.windows,self.acceptance)
        with self.assertRaises(ValueError): self.check("production",True)

    def test_missing_case_rejected(self):
        self.acceptance["cases"].pop()
        write_json(self.windows,self.acceptance)
        self.assertEqual(self.check("production",True)["status"],"fail")


class PackagePathTests(unittest.TestCase):
    def test_windows_unsafe_names_rejected(self):
        from Package import add_file
        for name in ("/absolute","a/../escape","a/./dot","a\\backslash","a/aux.txt","a/NUL", "a/COM1.txt",
                     "a/colon:name","a/trailing.","a/trailing ","a//empty","a/line\nfeed"):
            with self.subTest(name=name):
                with self.assertRaises(ValueError): add_file({},name,b"fixture")

    def test_windows_case_collision_rejected(self):
        from Package import add_file
        files={}
        add_file(files,"ManualDiceRoller/test.txt",b"one")
        with self.assertRaises(ValueError): add_file(files,"manualdiceroller/TEST.txt",b"one")

    def test_conflicting_identity_rejected(self):
        from Package import add_file
        files={}
        add_file(files,"a/test.txt",b"one")
        with self.assertRaises(ValueError): add_file(files,"a/test.txt",b"two")
        add_file(files,"a/test.txt",b"one")
        self.assertEqual(files,{"a/test.txt":b"one"})


if __name__ == "__main__":
    unittest.main(verbosity=2)
