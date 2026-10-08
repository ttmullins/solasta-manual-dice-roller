"""Validate fresh developer evidence and optional actual Windows acceptance.

Candidate qualification never implies production qualification. The production
gate requires tester-recorded outcomes, retained evidence and actual live
Windows/Mono diagnostics for the identical plugin. It does not run the game.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path, PureWindowsPath

from RunRegression import CORE_SUITES, source_hashes, suite_runners
from FixtureEvidence import inspect as inspect_fixture_evidence
from TestEnvironment import GAME_SHA256, NETWORK_SHA256, sha256


def load(path):
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value,dict):
        raise ValueError("Evidence/manifest JSON must be an object: " + str(path))
    return value


def wire_int(value):
    if type(value) is int:
        return value
    if isinstance(value,str) and value and len(value)<=12 and value.isdecimal():
        return int(value)
    raise ValueError("Invalid integer in retained network marker")


def wire_vector(value):
    if isinstance(value,str):
        value = value.split(",") if value else []
    if not isinstance(value,list) or len(value)>4096:
        raise ValueError("Invalid vector in retained network marker")
    return [wire_int(item) for item in value]


def local_file(directory, relative):
    path = (directory / relative).resolve()
    if directory.resolve() not in path.parents or not path.is_file():
        raise ValueError("Missing or escaping evidence file: " + str(relative))
    return path


def timestamp(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("Evidence timestamps must include a timezone")
    return result


def check_windows(project, build, path, *, require_cases=True):
    errors = []
    acceptance = load(path)
    folder = path.resolve().parent
    diagnostics = load(local_file(folder, acceptance.get("diagnostic_report", "")))
    definition = load(project / "tools/acceptance-cases.json")
    if acceptance.get("schema_version") != 1 or diagnostics.get("schema_version") != 1:
        errors.append("Unsupported Windows evidence schema")
    if acceptance.get("profile") != definition["profile"] or diagnostics.get("platform") != "windows":
        errors.append("Windows x64 Unity/Mono evidence profile is required")
    if not acceptance.get("tester", "").strip():
        errors.append("Windows acceptance must identify its tester")
    if acceptance.get("plugin_sha256") != build["plugin_sha256"] or acceptance.get("game_sha256") != GAME_SHA256 or acceptance.get("mod_version") != build["metadata"]["Version"]:
        errors.append("Acceptance identifies a different game/plugin/version")
    if diagnostics.get("setup_errors"):
        errors.append("Windows setup diagnostics contain unresolved errors")
    files = diagnostics.get("files", {})
    for key, expected in (("game", GAME_SHA256), ("plugin", build["plugin_sha256"]), ("info", build["info_sha256"]),
                          ("umm", build["umm_sha256"]), ("harmony", build["harmony_sha256"])):
        if files.get(key, {}).get("sha256") != expected:
            errors.append("Windows disk input does not match the audited build: " + key)
    for key,name in (("photon_pun","PhotonUnityNetworking.dll"),("photon_realtime","PhotonRealtime.dll"),("photon_client","Photon3Unity3D.dll")):
        if files.get(key,{}).get("sha256") != NETWORK_SHA256[name]:
            errors.append("Windows Photon transport differs from the audited game: " + key)
    expected_exe = str(PureWindowsPath(diagnostics.get("game_path", "")) / "Solasta.exe").lower()
    processes = [p for p in diagnostics.get("processes", []) if
                 str(PureWindowsPath(p.get("executable_path", ""))).lower() == expected_exe]
    if not processes:
        errors.append("A live Solasta process was not observed when diagnostics were collected")
    retained_markers = []
    for entry in diagnostics.get("logs", []):
        log = local_file(folder, entry["relative_path"])
        if sha256(log) != entry["sha256"]:
            errors.append("Collected log was changed after its diagnostics snapshot")
        for line in log.read_text(encoding="utf-8-sig").splitlines():
            if "MDR_DIAGNOSTICS " in line:
                retained_markers.append(json.loads(line.split("MDR_DIAGNOSTICS ", 1)[1]))
    markers = [entry["value"] for entry in diagnostics.get("runtime_markers", [])
               if entry.get("value", {}).get("plugin_sha256") == build["plugin_sha256"] and entry["value"] in retained_markers]
    if not markers:
        errors.append("Successful live MDR_DIAGNOSTICS marker for this artifact is missing")
    else:
        marker = max(markers, key=lambda m: timestamp(m.get("utc", "")))
        if (marker.get("version") != build["metadata"]["Version"] or marker.get("umm_version") != "0.32.4.0" or
                marker.get("harmony_version") != "2.3.6.0" or marker.get("umm_sha256") != build["umm_sha256"] or
                marker.get("harmony_sha256") != build["harmony_sha256"] or marker.get("mono_runtime") is not True or
                marker.get("pointer_size") != 8 or not marker.get("unity_version") or not marker.get("clr_version")):
            errors.append("Loaded artifact/loader/runtime marker does not meet the Windows Mono profile")
        collected = timestamp(diagnostics["collected_utc"])
        marked = timestamp(marker["utc"])
        process_starts = [timestamp(p["start_utc"]) for p in processes if p.get("start_utc")]
        if not process_starts or marked < min(process_starts) or marked > collected:
            errors.append("Runtime marker does not belong to the observed current process session")
        if timestamp(acceptance.get("session_started_utc", "")) > collected:
            errors.append("Acceptance session started after diagnostics were collected")
    cases = acceptance.get("cases", []) if require_cases else []
    ids = [case.get("id") for case in cases]
    required = [case["id"] for case in definition["cases"]]
    if require_cases and (len(ids) != len(set(ids)) or set(ids) != set(required)):
        errors.append("Acceptance case inventory is missing, duplicated or unexpected")
    evidence_hashes = {}
    for case in cases:
        if case.get("status") != "pass" or not case.get("observed", "").strip() or not case.get("evidence"):
            errors.append("Manual Windows outcome/evidence is incomplete: " + str(case.get("id")))
        for relative in case.get("evidence", []):
            file = local_file(folder, relative)
            if file.stat().st_size == 0:
                errors.append("Acceptance evidence is empty: " + str(relative))
            evidence_hashes[relative] = sha256(file)
    return errors, {"acceptance_sha256": sha256(path), "diagnostics_sha256": sha256(local_file(folder, acceptance["diagnostic_report"])),
                    "evidence_sha256": evidence_hashes, "basis": "tester_recorded_actual_Windows_game_observations"}


def check_multiplayer(project, build, path):
    """Check actual per-peer evidence; never substitute simulated transports."""
    errors = []
    acceptance = load(path)
    folder = path.resolve().parent
    definition = load(project / "tools/multiplayer-acceptance-cases.json")
    protocol_version = definition["protocol"]
    if type(protocol_version) is not int or protocol_version < 1:
        raise ValueError("The multiplayer acceptance definition needs a positive protocol version")
    if acceptance.get("schema_version") != 1 or acceptance.get("profile") != definition["profile"]:
        errors.append("Actual coordinated multiplayer Windows evidence profile is required")
    if not acceptance.get("tester", "").strip() or not acceptance.get("session", "").strip():
        errors.append("Multiplayer tester and negotiated session must be recorded")
    if (acceptance.get("plugin_sha256") != build["plugin_sha256"] or acceptance.get("game_sha256") != GAME_SHA256 or
            acceptance.get("mod_version") != build["metadata"]["Version"] or acceptance.get("protocol") != protocol_version):
        errors.append("Multiplayer report identifies a different artifact/game/protocol")
    started = timestamp(acceptance.get("session_started_utc", ""))
    peers = acceptance.get("peers", [])
    actors = [p.get("actor") for p in peers]
    names = [p.get("name") for p in peers]
    roles = [p.get("role") for p in peers]
    if (len(peers) < 2 or len(actors) != len(set(actors)) or len(names) != len(set(names)) or
            any(type(a) is not int or a <= 0 for a in actors) or any(not isinstance(n,str) or not n.strip() for n in names) or
            roles.count("host") != 1 or any(role not in ("host","client") for role in roles) or "client" not in roles):
        errors.append("Distinct actual host and client peer identities are required")
    master = next((p.get("actor") for p in peers if p.get("role") == "host"), None)
    retained = {}
    profiles = set()
    rooms = set()
    hashes = {}
    reports = []
    for peer in peers:
        peer_path = local_file(folder, peer.get("acceptance_report", ""))
        reports.append(peer_path)
        issues, evidence = check_windows(project,build,peer_path,require_cases=False)
        errors.extend(str(peer.get("name")) + ": " + issue for issue in issues)
        hashes[peer["acceptance_report"]] = evidence
        peer_acceptance = load(peer_path)
        diagnostics = load(local_file(peer_path.parent,peer_acceptance["diagnostic_report"]))
        markers = []
        for entry in diagnostics.get("logs", []):
            log = local_file(peer_path.parent,entry["relative_path"])
            for line in log.read_text(encoding="utf-8-sig").splitlines():
                if "MDR_NETWORK " in line:
                    markers.append(json.loads(line.split("MDR_NETWORK ",1)[1]))
        process_starts = [timestamp(p["start_utc"]) for p in diagnostics.get("processes",[]) if p.get("start_utc")]
        collected = timestamp(diagnostics["collected_utc"])
        ready = [m for m in markers if m.get("event") == "session_ready" and m.get("session") == acceptance.get("session")
                 and wire_int(m.get("local_actor")) == peer.get("actor") and wire_int(m.get("master_actor")) == master
                 and wire_int(m.get("protocol")) == protocol_version and m.get("plugin_sha256") == build["plugin_sha256"]
                 and m.get("game_sha256") == GAME_SHA256 and sorted(wire_vector(m.get("members"))) == sorted(actors)
                 and process_starts and max(started,min(process_starts)) <= timestamp(m.get("utc","")) <= collected]
        if not ready:
            errors.append("Retained exact-profile all-peer session-ready marker is missing: " + str(peer.get("name")))
        for marker in ready:
            profiles.add(marker.get("profile"))
            rooms.add(marker.get("room_sha256"))
        retained[peer.get("actor")] = markers
    if len(reports) != len(set(reports)):
        errors.append("Different peers cannot reuse the same diagnostics/acceptance report")
    if (len(profiles)!=1 or len(rooms)!=1 or any(not isinstance(v,str) or len(v)!=64 or
            any(c not in "0123456789abcdef" for c in v) for v in profiles | rooms)):
        errors.append("Every peer must retain the same exact room and artifact/settings profile fingerprints")
    cases = acceptance.get("cases",[])
    ids = [case.get("id") for case in cases]
    required = [case["id"] for case in definition["cases"]]
    if len(ids) != len(set(ids)) or set(ids) != set(required):
        errors.append("Multiplayer acceptance case inventory is missing, duplicated or unexpected")
    evidence_hashes = {}
    for case in cases:
        if (case.get("status") != "pass" or not case.get("observed","").strip() or not case.get("evidence") or
                sorted(case.get("peer_actors",[])) != sorted(actors)):
            errors.append("Actual all-peer multiplayer observation/evidence is incomplete: " + str(case.get("id")))
        for relative in case.get("evidence",[]):
            evidence = local_file(folder,relative)
            if evidence.stat().st_size == 0:
                errors.append("Multiplayer evidence file is empty: " + str(relative))
            evidence_hashes[relative] = sha256(evidence)
    rolls = acceptance.get("recorded_rolls",[])
    required_roll_cases = set(definition["recorded_roll_cases"])
    if not required_roll_cases.issubset({roll.get("case_id") for roll in rolls}):
        errors.append("Known-face multiplayer comparison records are incomplete")
    seen = set()
    for roll in rolls:
        key = (roll.get("action"),roll.get("sequence"),roll.get("signature"))
        observations = roll.get("peers",[])
        if key in seen or not all(isinstance(v,str) and v.strip() for v in (roll.get("action"),roll.get("signature"))):
            errors.append("Duplicate or missing multiplayer action/descriptor identity")
        seen.add(key)
        if type(roll.get("sequence")) is not int or roll["sequence"] < 0 or roll.get("owner") not in actors:
            errors.append("Invalid multiplayer sequence or controller identity")
        if sorted(p.get("actor") for p in observations) != sorted(actors):
            errors.append("Known-face comparison omits or duplicates a peer")
        vectors = [p.get("faces") for p in observations]
        results = [p.get("native_result") for p in observations]
        if (not vectors or any(not isinstance(v,list) or not v or len(v)>4096 or
            any(type(face) is not int or face<1 or face>100 for face in v) for v in vectors) or any(v != vectors[0] for v in vectors)):
            errors.append("Multiplayer peers did not record the same complete raw-face vector")
        if not results or any(not isinstance(v,str) or not v.strip() or v != results[0] for v in results):
            errors.append("Multiplayer native outcomes differ or are missing")
        for field in ("native_rng_draws","native_callbacks"):
            expected = roll.get("expected_" + field)
            if type(expected) is not int or expected < 0 or any(p.get(field) != expected or type(p.get(field)) is not int for p in observations):
                errors.append("Multiplayer native RNG/callback baseline differs: " + field)
        for observation in observations:
            completed = [m for m in retained.get(observation.get("actor"),[]) if m.get("event") == "roll_completed" and
                         m.get("session") == acceptance.get("session") and wire_int(m.get("sequence")) == roll.get("sequence") and
                         m.get("signature") == roll.get("signature") and m.get("action") == roll.get("action") and
                         wire_int(m.get("owner")) == roll.get("owner") and wire_vector(m.get("faces")) == observation.get("faces")]
            if not completed:
                errors.append("Retained exact-action/owner/vector completion marker is missing on peer " + str(observation.get("actor")))
        if not roll.get("baseline_evidence"):
            errors.append("Native RNG/callback baseline needs actual retained observations")
        for relative in roll.get("baseline_evidence",[]):
            evidence = local_file(folder,relative)
            if evidence.stat().st_size == 0: errors.append("Empty native baseline evidence")
            evidence_hashes[relative] = sha256(evidence)
    return errors, {"acceptance_sha256":sha256(path),"peer_evidence":hashes,"evidence_sha256":evidence_hashes,
                    "basis":"tester_recorded_actual_multi_peer_Windows_Photon_observations"}


def validate(project, build_dir, regression_path, level, windows_path=None, multiplayer_path=None):
    errors = []
    build = load(build_dir / "build-manifest.json")
    if build.get("game_sha256") != GAME_SHA256:
        errors.append("Build manifest identifies a different native game")
    required_audits = {"dependencies.txt","network-abi.txt","harmony-generator-probe.txt","assembly-audit.txt",
                       "harmony-bindings.txt","harmony-bindings-negative-name.txt","harmony-bindings-negative-type.txt","harmony-native-read.txt"}
    if not required_audits.issubset(build.get("audits",{})):
        errors.append("The complete audited runtime/dependency/binding evidence inventory is required")
    for name,expected in NETWORK_SHA256.items():
        if build.get("managed_references",{}).get(name) != expected:
            errors.append("Build manifest identifies a different pristine Photon input: " + name)
    for name, expected in build.get("audits", {}).items():
        if Path(name).name != name or sha256(build_dir / name) != expected:
            errors.append("Audited build evidence changed: " + name)
    plugin = build_dir / "ManualDiceRoller/ManualDiceRoller.dll"
    info = build_dir / "ManualDiceRoller/Info.json"
    if sha256(plugin) != build["plugin_sha256"] or sha256(info) != build["info_sha256"] or load(info) != build["metadata"]:
        errors.append("Build artifact/metadata changed after the audited build")
    current = source_hashes(project)
    actual_sources = {name.removeprefix("src/"): value for name, value in current.items() if name.startswith("src/") and name.endswith(".cs")}
    if actual_sources != build["sources"]:
        errors.append("Runtime sources differ from the compiled snapshot")
    current_tools = {p.name: sha256(p) for p in (project / "tools").iterdir()
                     if p.is_file() and p.suffix in (".cs", ".py", ".ps1", ".json", ".md") and not p.name.endswith("-reference.cs")}
    if current_tools != build["tools"]:
        errors.append("Build/audit/package/tool inventory or bytes differ from the audited build")
    if load(project / "src/info.template.json") != build["metadata"]:
        errors.append("Source metadata differs from generated Info.json")
    regression = load(regression_path)
    fixture_evidence = inspect_fixture_evidence(project, build.get("managed_references", {}))
    errors.extend(fixture_evidence["errors"])
    if (fixture_evidence["status"] != "pass" or regression.get("fixture_evidence") != fixture_evidence or
            regression.get("fixture_evidence_unchanged") is not True):
        errors.append("Fresh unchanged fixture manifest inventory and recorded source/result hashes are required")
    inventory = sorted(suite_runners(project))
    if (regression.get("status") != "pass" or not regression.get("full_inventory_run") or
            regression.get("suite_inventory") != inventory or regression.get("selected_suites") != inventory or
            not CORE_SUITES.issubset(inventory)):
        errors.append("A fresh passing run of the complete current developer suite inventory is required")
    if set(regression.get("suites",{})) != set(inventory):
        errors.append("Every discovered developer suite must have its own passing retained result/log")
    if regression.get("sources_before") != current or regression.get("sources_after") != current:
        errors.append("Developer evidence does not describe the current exact source/tool/test bytes")
    if not regression.get("managed_inputs_unchanged") or not regression.get("environment_inputs_unchanged"):
        errors.append("Developer input dependencies changed during execution")
    if (regression.get("managed_inputs_before") != build.get("managed_references") or
            regression.get("managed_inputs_after") != build.get("managed_references")):
        errors.append("Developer suites and runtime build used different managed reference inputs")
    for name, entry in regression.get("suites", {}).items():
        if entry.get("status") != "pass" or sha256(local_file(regression_path.parent, entry["log"])) != entry["log_sha256"]:
            errors.append("Developer suite/log did not pass unchanged: " + name)
    for name in ("tool_regressions", "powershell_grammar", "build", "package_regressions"):
        entry = regression.get(name)
        if entry and (entry.get("status") != "pass" or sha256(local_file(regression_path.parent, entry["log"])) != entry["log_sha256"]):
            errors.append("Developer operation/log did not pass unchanged: " + name)
        if entry is None:
            errors.append("Required release operation was not run: " + name)
    for name in ("assembly-audit.txt", "harmony-bindings.txt"):
        if not (build_dir / name).read_text().rstrip().endswith("0 errors."):
            errors.append("Build audit did not pass: " + name)
    if not (build_dir / "network-abi.txt").read_text().rstrip().endswith("0 errors."):
        errors.append("Pristine Photon runtime ABI audit did not pass")
    if "; 0 errors." not in (build_dir / "harmony-native-read.txt").read_text().splitlines()[-1]:
        errors.append("Actual native Harmony metadata/IL read audit did not pass")
    windows = None
    multiplayer = None
    if level == "production":
        if windows_path is None:
            errors.append("Actual Windows Unity/Mono acceptance is required for production qualification")
        else:
            issues, windows = check_windows(project, build, windows_path)
            errors.extend(issues)
        if multiplayer_path is None:
            errors.append("Actual coordinated host/client Windows multiplayer acceptance is required for production qualification")
        else:
            issues, multiplayer = check_multiplayer(project,build,multiplayer_path)
            errors.extend(issues)
    return {"schema_version": 1, "level": level, "status": "pass" if not errors else "fail",
            "version": build["metadata"]["Version"], "plugin_sha256": build["plugin_sha256"],
            "build_manifest_sha256": sha256(build_dir / "build-manifest.json"),
            "regression_report_sha256": sha256(regression_path), "windows_evidence": windows,
            "multiplayer_evidence": multiplayer, "live_multiplayer_qualified": level == "production" and not errors,
            "live_windows_mono_qualified": level == "production" and not errors,
            "errors": errors, "evidence_finished_utc": regression.get("finished_utc")}


def main():
    project = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, default=project / "build")
    parser.add_argument("--regressions", type=Path, required=True)
    parser.add_argument("--level", choices=("candidate", "production"), default="candidate")
    parser.add_argument("--windows", type=Path)
    parser.add_argument("--multiplayer", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = validate(project, args.build.resolve(), args.regressions.resolve(), args.level,
                          args.windows.resolve() if args.windows else None,
                          args.multiplayer.resolve() if args.multiplayer else None)
    except (ValueError, KeyError, OSError, TypeError, AttributeError, OverflowError) as error:
        report = {"status": "fail", "level": args.level, "live_windows_mono_qualified": False, "live_multiplayer_qualified": False,
                  "errors": ["Invalid/missing evidence: " + str(error)]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report["validated_utc"] = datetime.now(timezone.utc).isoformat()
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("Release " + args.level + " gate: " + report["status"])
    for error in report["errors"]:
        print("- " + error)
    return 0 if report["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
