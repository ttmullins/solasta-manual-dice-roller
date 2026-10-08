#!/usr/bin/env python3
"""Connect production multiplayer/runtime code in separate native-dice peers."""
from pathlib import Path
import argparse
import base64
import hashlib
import json
import random
import shutil
import subprocess
import sys

here = Path(__file__).resolve().parent
project = here.parents[1]
sys.path.insert(0, str(project / 'tools'))
from TestEnvironment import resolve

parser = argparse.ArgumentParser(description=__doc__)
for key in ('dotnet', 'sdk-root', 'managed', 'references', 'cecil'):
    parser.add_argument('--' + key, type=Path)
args = parser.parse_args()
env = resolve(project, dotnet=args.dotnet, sdk_root=args.sdk_root, managed=args.managed,
              references=args.references, cecil=args.cecil)
out = here / 'build'
out.mkdir(exist_ok=True)


def invoke(command):
    result = subprocess.run([str(x) for x in command], text=True, capture_output=True)
    if result.returncode:
        print(result.stdout + result.stderr, end='')
    result.check_returncode()
    return result.stdout


def compile_cs(name, sources, extra=(), executable=False):
    response = out / (name + '.rsp')
    response.write_text('\n'.join(['-nologo', '-nostdlib+', '-optimize+', '-langversion:7.3',
        '-target:' + ('exe' if executable else 'library'), '-out:"' + str(out / name) + '"'] +
        ['-r:"' + str(x) + '"' for x in env.net_refs + list(extra)] +
        ['"' + str(x) + '"' for x in sources]))
    (out / (name + '.compile.txt')).write_text(invoke([env.dotnet, env.compiler, '@' + str(response)]))
    if executable:
        (out / (Path(name).stem + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {
            'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '8.0.0'}}}))


# Reuse the independently checked native declaration/extraction pipeline,
# keeping every local addition explicit in this suite's generated fixture.
runtime = project / 'tests/network-native/runtime'
invoke([sys.executable, runtime / 'run.py', '--dotnet', env.dotnet,
        '--sdk-root', env.sdk_root, '--managed', env.managed, '--references', env.references,
        '--cecil', env.cecil])
native_text = (runtime / 'build/NativeNetworkRuntimeTypes.cs').read_text()
native_text = native_text.replace('public class RulesetActor\n{', 'public class RulesetActor\n{\n    public ulong Guid;')
native_text = native_text.replace('public static TestGameService Current;',
                                'public static TestGameService Current;public static FixtureOwners Players;')
native_text = native_text.replace(':(T)(object)Rules;',
                                ':typeof(T)==typeof(IPlayerControllerService)?(T)(object)Players:typeof(T)==typeof(INetworkingService)?default(T):(T)(object)Rules;')
native_text += '''
public interface INetworkingService {bool LeaveRoom();}
public class PlayerController {public enum ControllerType {Human,AI}public ControllerType Type;}
public interface IPlayerControllerService {int GetPlayerControllingHero(RulesetCharacter actor);System.Collections.Generic.Dictionary<int,PlayerController> ControllersById{get;}}
public class FixtureOwners:IPlayerControllerService {
public int Owner=1;public System.Collections.Generic.Dictionary<int,PlayerController> ControllersById{get;private set;}=new System.Collections.Generic.Dictionary<int,PlayerController>();
public FixtureOwners(){for(int i=1;i<=16;i++)ControllersById.Add(i,new PlayerController{Type=PlayerController.ControllerType.Human});}
public int GetPlayerControllingHero(RulesetCharacter actor){return Owner;}
}
'''
(out / 'ConnectedNativeTypes.cs').write_text(native_text)
shutil.copyfile(env.cecil, out / 'Mono.Cecil.dll')
shutil.copyfile(env.references / 'net8-harmony/0Harmony.dll', out / '0Harmony.dll')
compile_cs('Assembly-CSharp.dll', [out / 'ConnectedNativeTypes.cs'])
for name, source in [('ExtractNativeDice', project / 'tests/native/ExtractNativeDice.cs'),
                     ('ExtractNetworkNative', project / 'tests/network-native/ExtractNetworkNative.cs')]:
    compile_cs(name + '.dll', [source], [out / 'Mono.Cecil.dll'], True)
    (out / (name + '.txt')).write_text(invoke([env.dotnet, out / (name + '.dll'), env.managed / 'Assembly-CSharp.dll', out / 'Assembly-CSharp.dll']))
    shutil.move(str(out / 'Assembly-CSharp.dll.native'), str(out / 'Assembly-CSharp.dll'))

production = [project / 'src' / name for name in ('RollBroker.cs', 'RuntimeRolls.cs', 'RollText.cs', 'NativeScratchScope.cs',
    'NativeNetworkScheduler.cs', 'MultiplayerProtocol.cs', 'MultiplayerTransport.cs', 'MultiplayerRolls.cs')]
snapshot = out / 'source-snapshot'
snapshot.mkdir(exist_ok=True)
hashes = {}
for source in production:
    data = source.read_bytes()
    (snapshot / source.name).write_bytes(data)
    hashes[source.relative_to(project).as_posix()] = hashlib.sha256(data).hexdigest()
photon = [env.managed / name for name in ('Photon3Unity3D.dll', 'PhotonRealtime.dll', 'PhotonUnityNetworking.dll')]
for path in photon + [env.references / 'UnityModManager.dll']:
    shutil.copyfile(path, out / path.name)
compile_cs('PeerProcess.dll', [snapshot / p.name for p in production] + [here / 'PeerHost.cs', here / 'PeerProcess.cs',
    project / 'tests/common/OfflineBardicHost.cs', project / 'tests/common/OfflineShoveHost.cs',
    project / 'tools/HarmonyFixtureHost.cs'], [out / 'Assembly-CSharp.dll', out / '0Harmony.dll',
    env.references / 'UnityModManager.dll'] + photon, True)

checks = 0
scenarios = []


def check(value, message):
    global checks
    checks += 1
    if not value:
        raise AssertionError(message)


def connected(count, mode, seed, expect_divergence=False):
    workers = {}
    error_files = {}
    pending = {actor: [] for actor in range(1, count + 1)}
    reports = {actor: {} for actor in pending}
    done = set()
    rng = random.Random(seed)
    try:
        for actor in pending:
            errors = open(out / f'peer-{count}-{mode}-{actor}.stderr.txt', 'w')
            error_files[actor] = errors
            workers[actor] = subprocess.Popen([str(env.dotnet), str(out / 'PeerProcess.dll'), str(actor), str(count), mode],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors, text=True, bufsize=1)
        for frame in range(3000):
            # Transport loss/reordering is deliberately stronger than a normal
            # reliable Photon channel; production retries must recover safely.
            for actor in list(pending):
                packets = pending[actor]
                pending[actor] = []
                rng.shuffle(packets)
                if packets and rng.randrange(9) == 0:
                    packets.pop()
                if packets and rng.randrange(8) == 0:
                    packets.append(dict(packets[0]))
                worker = workers[actor]
                # Unity runs 0..3 fixed steps (game ticks) per rendered frame,
                # independently on every computer.
                steps = rng.choice((0, 1, 1, 1, 2, 3))
                worker.stdin.write(json.dumps({'packets': packets, 'steps': steps}) + '\n')
                worker.stdin.flush()
                line = worker.stdout.readline()
                # Harmony's declared development-host notice is not IPC data.
                while line and not line.startswith('{'):
                    line = worker.stdout.readline()
                if not line:
                    error_files[actor].flush()
                    raise AssertionError(f'Peer {actor} stopped: ' + (out / f'peer-{count}-{mode}-{actor}.stderr.txt').read_text())
                message = json.loads(line)
                check(not message['halted'], f'normal connected room halted at step {frame}')
                for packet in message['sends']:
                    check(packet['sender'] == actor and packet['target'] != actor, 'transport sender/recipient identity')
                    check(len(base64.b64decode(packet['payload'])) <= 16384, 'bounded actual production packet')
                    pending[packet['target']].append(packet)
                for report in message['reports']:
                    check(report['round'] not in reports[actor], 'native invocation completed more than once')
                    reports[actor][report['round']] = report
                if message['done']:
                    done.add(actor)
            if len(done) == count:
                break
        check(len(done) == count, 'connected peers failed to finish the bounded action schedule')
        if expect_divergence:
            # The 0.8.2 behaviour (ticks keep running while a roll waits): the
            # same shared rolls run at different ticks after different amounts
            # of other gameplay. The lockstep checks below must catch this.
            diverged = [step for step in range(18) if len({(reports[a][step]['tick'], reports[a][step]['other'], reports[a][step]['state']) for a in pending}) > 1]
            check(len(diverged) > 0, f'{mode}: running ticks during shared rolls did not diverge; the lockstep checks would be blind')
            scenarios.append({'members': count, 'mode': mode, 'frames': frame + 1, 'native_steps': 18,
                              'diverged_steps_without_lockstep': len(diverged)})
            (out / f'observations-{count}-{mode}.json').write_text(json.dumps(reports, indent=2) + '\n')
            return
        expected_state = 320
        for step in range(18):
            baseline = reports[1][step]
            owner = 1 + step % count
            for actor in pending:
                observation = reports[actor][step]
                check(observation['owner'] == owner, 'wrong native owner entered an action')
                # 0.8.3 lockstep: the same tick, after the same other gameplay.
                for key in ('tick', 'other', 'result', 'draws', 'next', 'state', 'auto'):
                    if key == 'result' and mode == 'different-result':
                        # Peer 2's game reaches another result with the same
                        # dice (the game's own sync): reported, never fatal.
                        check(observation['result'] == baseline['result'] + (1 if actor == 2 else 0), f'different-result step {step}: injected result')
                        continue
                    check(observation[key] == baseline[key], f'connected peer native {key} diverged at step {step}')
                check(not observation.get('heldAdvanced'), 'a held native coroutine advanced while a deferred roll was pending')
                check(observation.get('deferred') == (mode == 'deferred'), 'deferred scenario identity')
                check(observation.get('extended') == (mode == 'extended'), 'extended scenario identity')
                if mode == 'extended':
                    # Who enters faces: the hero's controller, the room host for
                    # the game master's dice (steps 2 and 4), nobody else.
                    prompted = {0: {owner}, 1: {owner}, 2: {owner, 1}, 3: {owner}, 4: {1}, 5: {owner}}[step % 6]
                    check((observation['prompts'] > 0) == (actor in prompted), f'extended step {step}: only the dice owners enter faces')
                    if prompted == {owner} and step % 6 != 2:
                        check((observation['waiting'] > 0) == (actor != owner), 'observing peer waiting state')
                    continue
                if mode == 'different-plan' and step % 6 == 0:
                    # Peer 2 planned this step differently: nobody is asked,
                    # every peer lets the game roll, and nobody pauses.
                    check(observation['prompts'] == 0 and observation['waiting'] == 0 and not observation['paused'],
                          f'different-plan step {step}: a plan-only difference must fall back to the game for everyone')
                    continue
                check((observation['prompts'] > 0) == (actor == owner), 'only the current hero controller may enter faces')
                check((observation['waiting'] > 0) == (actor != owner), 'observing peer waiting state')
            if mode == 'extended':
                # Success asks the food dice (15+3+4); failure skips them (4);
                # hero 11 + host 6; Bless entered 2 replaces the rolled 3 (12+5-3+2);
                # the host's 7+8; a plain d20.
                check(baseline['result'] == [22, 4, 17, 16, 15, 12][step % 6], f'extended step {step}: agreed faces did not reach native evaluation ({baseline["result"]})')
                check(baseline['draws'] == [3, 1, 2, 1, 2, 1][step % 6], f'extended step {step}: native draw consumption ({baseline["draws"]})')
            elif not baseline['auto'] and not (mode == 'different-plan' and step % 6 == 0):
                expected_results = [17, 19, 40, 11, 14, None]
                expected_draws = [2, 3, 5, 3, 3, 2]
                if expected_results[step % 6] is not None:
                    check(baseline['result'] == expected_results[step % 6], 'accepted physical faces did not reach native evaluation')
                check(baseline['draws'] == expected_draws[step % 6], 'original scalar RNG consumption did not match the manual native draw plan')
            if mode.startswith('lockstep'):
                # Other gameplay draws every tick: the peers' equality above is
                # the check; the state is no longer a function of the steps alone.
                continue
            for _ in range(baseline['draws'] + 1):
                expected_state = (expected_state * 1664525 + 1013904223) & 0xffffffff
            check(baseline['state'] == expected_state, 'native deterministic state restore/record differs from sampled raw draws')
        scenarios.append({'members': count, 'mode': mode, 'frames': frame + 1, 'native_steps': 18,
                          'last_tick': reports[1][17]['tick'],
                          'all_peer_native_results_ticks_and_following_automatic_draws_match': True})
        (out / f'observations-{count}-{mode}.json').write_text(json.dumps(reports, indent=2) + '\n')
    finally:
        for worker in workers.values():
            if worker.poll() is None:
                worker.stdin.close()
                try:
                    worker.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    worker.kill()
                    worker.wait()
        for errors in error_files.values():
            errors.close()


def connected_failure(mode):
    """0.8.1: a disagreement pauses manual dice instead of stopping the room.

    Every peer must pause, stay in the room, never throw into the native
    coroutine, finish the bounded schedule with the game's dice and keep the
    shared deterministic state aligned (except the injected result itself)."""
    workers = {}
    errors = {}
    pending = {1: [], 2: []}
    paused = {}
    reports = {1: {}, 2: {}}
    done = set()
    try:
        for actor in pending:
            errors[actor] = open(out / f'negative-{mode}-{actor}.stderr.txt', 'w')
            workers[actor] = subprocess.Popen([str(env.dotnet), str(out / 'PeerProcess.dll'), str(actor), '2', mode],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors[actor], text=True, bufsize=1)
        for frame in range(4000):
            for actor, worker in workers.items():
                packets = pending[actor]
                pending[actor] = []
                worker.stdin.write(json.dumps({'packets': packets}) + '\n')
                worker.stdin.flush()
                line = worker.stdout.readline()
                while line and not line.startswith('{'):
                    line = worker.stdout.readline()
                if not line:
                    errors[actor].flush()
                    raise AssertionError(f'{mode}: peer {actor} broke its native coroutine: ' + (out / f'negative-{mode}-{actor}.stderr.txt').read_text())
                message = json.loads(line)
                check(message['inRoom'], f'{mode}: a disagreement must never make a player leave the room')
                for packet in message['sends']:
                    pending[packet['target']].append(packet)
                for report in message['reports']:
                    check(report['round'] not in reports[actor], f'{mode}: native invocation completed more than once')
                    reports[actor][report['round']] = report
                if message['halted'] and actor not in paused:
                    paused[actor] = len(reports[actor])
                if message['done']:
                    done.add(actor)
            if len(done) == 2:
                break
        check(len(paused) == 2, f'{mode} did not pause every peer')
        check(len(done) == 2, f'{mode}: the game did not play on with its own dice after the pause')
        first_paused = min(paused.values())
        for step in range(18):
            one, two = reports[1][step], reports[2][step]
            for key in ('draws', 'next', 'state'):
                check(one[key] == two[key], f'{mode}: shared deterministic {key} diverged at step {step}')
            if mode != 'different-result':
                check(one['result'] == two['result'], f'{mode}: game-dice result diverged at step {step}')
            if step > first_paused:
                check(one['prompts'] == 0 and two['prompts'] == 0 and one['paused'] and two['paused'], f'{mode}: step {step} asked for dice after the pause')
        return {'mode': mode, 'frames': frame + 1, 'paused_at_step': first_paused,
                'all_peers_paused_stayed_in_room_and_finished_with_game_dice': True}
    finally:
        for worker in workers.values():
            if worker.poll() is None:
                worker.stdin.close()
                try:
                    worker.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    worker.kill()
                    worker.wait()
        for handle in errors.values():
            handle.close()


connected(2, 'manual', 13)
connected(4, 'manual', 41)
connected(2, 'auto', 83)
connected(2, 'deferred', 97)
connected(3, 'deferred', 131)
connected(3, 'extended', 173)
connected(2, 'different-plan', 191)
connected(2, 'different-result', 211)
connected(2, 'lockstep', 223)
connected(3, 'lockstep', 227)
connected(4, 'lockstep', 229)
connected(2, 'lockstep-late', 233)
connected(3, 'lockstep-late', 239)
connected(2, 'no-lockstep', 241, expect_divergence=True)
different = json.loads((out / 'observations-2-different-result.json').read_text())
check(all(different[str(actor)]['17']['differences'] == 18 for actor in (1, 2)), 'every differing result was reported on every peer without pausing')
check(all(json.loads((out / f'observations-{n}-{m}.json').read_text())[str(a)]['17']['differences'] == 0
          for n, m in ((2, 'manual'), (2, 'auto'), (2, 'different-plan')) for a in (1, 2)), 'matching results are never reported as different')
negative_scenarios = [connected_failure(mode) for mode in ('different-profile', 'different-owner', 'transfer')]
for source in production:
    check(hashes[source.relative_to(project).as_posix()] == hashlib.sha256(source.read_bytes()).hexdigest(), 'production source changed during connected execution')
lockstep_rooms = [entry for entry in scenarios if 'diverged_steps_without_lockstep' not in entry]
negative_lockstep = [entry for entry in scenarios if 'diverged_steps_without_lockstep' in entry]
scenarios = lockstep_rooms
shared_steps = 18 * len(scenarios)
continuations = 18 * sum(entry['members'] for entry in scenarios)
result = (f'PASS connected-process multiplayer/native integration: {checks} assertions; '
          f'{len(scenarios)} passing rooms and {len(negative_scenarios)} paused disagreement rooms (every peer stays in the room and the game rolls on); {shared_steps} shared native steps, {continuations} per-peer native continuations, including deferred runner steps.\n'
          f'Lockstep: every peer runs a random 0-3 game ticks per frame; in every passing room each shared roll ran at the same game tick after the same other gameplay on every peer; '
          f'without the stopped ticks (0.8.2 behaviour) {negative_lockstep[0]["diverged_steps_without_lockstep"]} of 18 steps diverged.\n'
          'Executed production protocol/transport/facade/broker/runtime/scheduler with pristine copied raw dice/RNG IL.\n'
          'Boundary: local queued gateway, upstream driver/planners/descriptors/GUI and Unity random API; no live Photon/Unity/Windows qualification.\n')
(here / 'results.txt').write_text(result)
(here / 'results.json').write_text(json.dumps({'status': 'pass', 'assertions': checks, 'scenarios': scenarios, 'negative_scenarios': negative_scenarios,
    'negative_lockstep': negative_lockstep}, indent=2) + '\n')
fixture_inputs = [here / 'PeerHost.cs', here / 'PeerProcess.cs', here / 'run.py', here / 'README.md',
                  runtime / 'run.py', project / 'tests/native/NativeDiceTypes.cs',
                  project / 'tests/native/ExtractNativeDice.cs', project / 'tests/network-native/NetworkNativeTypes.cs',
                  project / 'tests/network-native/ExtractNetworkNative.cs', project / 'tests/common/OfflineBardicHost.cs',
                  project / 'tests/common/OfflineShoveHost.cs', project / 'tools/HarmonyFixtureHost.cs']
(here / 'source-manifest.json').write_text(json.dumps({'status': 'pass', 'production_sources': hashes,
    'fixture_sources': {p.relative_to(project).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in fixture_inputs},
    'pristine_game_sha256': hashlib.sha256((env.managed / 'Assembly-CSharp.dll').read_bytes()).hexdigest(),
    'generated_fixture_sha256': hashlib.sha256(native_text.encode()).hexdigest(),
    'photon_assembly_hashes': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in photon},
    'results_sha256': hashlib.sha256(result.encode()).hexdigest(),
    'actual_photon_server_or_windows_unity_executed': False}, indent=2) + '\n')
print(result, end='')
