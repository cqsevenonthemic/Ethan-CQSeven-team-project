using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Headless regression test for StateMachineTrigger.ValidateDeliveryAt destination gating.
    /// Triggered only by the command-line flag -runDeliveryTest (no scene changes).
    /// Covers the P4 Live failure: QuYuan starts at (7,7), ZhaoJun starts at (8,8);
    /// carrying ZhaoJun through Chu (8,8) must NOT emit delivery_rejected.
    /// </summary>
    public sealed class DeliveryValidationTestRunner : MonoBehaviour
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private StateMachineTrigger _sm;
        private MethodInfo _validate;
        private PropertyInfo _carrySlot;
        private FieldInfo _rejectedCell;
        private Dictionary<string, string> _truthRoles;
        private Queue<NextActionEventDto> _pending;
        private readonly List<DeliveryValidationEvent> _events = new List<DeliveryValidationEvent>();
        private int _passed;
        private int _failed;

        private bool _isLiveE2E;
        private bool _isMockE2E;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRegisterIfCommandLineRequested()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                var isUnit = string.Equals(arg, "-runDeliveryTest", StringComparison.OrdinalIgnoreCase);
                var isLive = string.Equals(arg, "-runLiveTest", StringComparison.OrdinalIgnoreCase);
                var isMock = string.Equals(arg, "-runMockTest", StringComparison.OrdinalIgnoreCase);
                if (isUnit || isLive || isMock)
                {
                    var go = new GameObject("DeliveryValidationTestRunner");
                    var runner = go.AddComponent<DeliveryValidationTestRunner>();
                    runner._isLiveE2E = isLive;
                    runner._isMockE2E = isMock;
                    DontDestroyOnLoad(go);
                    return;
                }
            }
        }

        private IEnumerator Start()
        {
            if (_isLiveE2E || _isMockE2E)
            {
                yield return RunLiveEndToEndTest(_isLiveE2E);
                yield break;
            }
            Debug.Log("[DeliveryTest] ==================================================");
            Debug.Log("[DeliveryTest] STARTING ValidateDeliveryAt DESTINATION-GATING TEST");
            Debug.Log("[DeliveryTest] ==================================================");

            var timeout = Time.realtimeSinceStartup + 10f;
            while ((_sm = FindFirstObjectByType<StateMachineTrigger>()) == null || !_sm.IsReady)
            {
                if (Time.realtimeSinceStartup > timeout)
                {
                    Debug.LogError("[DeliveryTest] StateMachineTrigger never became ready.");
                    Quit(1);
                    yield break;
                }
                yield return null;
            }

            var t = typeof(StateMachineTrigger);
            _validate = t.GetMethod("ValidateDeliveryAt", Any);
            _carrySlot = t.GetProperty("CarrySlot", Any);
            _rejectedCell = t.GetField("_rejectedCellUntilLeave", Any);
            _truthRoles = (Dictionary<string, string>)t.GetField("_truthRoles", Any).GetValue(_sm);
            _pending = (Queue<NextActionEventDto>)t.GetField("_pendingEvents", Any).GetValue(_sm);
            _sm.DeliveryValidated += e => _events.Add(e);

            var chu = MazeLayout.QuYuanDestination;   // (8,8)
            var han = MazeLayout.ZhaoJunDestination;  // (7,7)
            var quyuan = ProtocolConstants.Roles.QuYuan;
            var zhaojun = ProtocolConstants.Roles.ZhaoJun;

            // Case 1: exact P4 Live repro - ZhaoJun (truth+pred) starts on Chu (8,8), QuYuan on Han (7,7).
            Setup("s2", zhaojun, zhaojun);
            Enter(chu);
            Expect("C1 carry ZhaoJun at Chu(8,8): no reject / no delivery", NoEvent() && Carry() == "s2");
            Leave();
            Enter(han);
            Expect("C1 carry ZhaoJun at Han(7,7): accepted", LastAccepted("s2") && Carry() == null && _pending.Count == 0);

            // Case 2: QuYuan (truth+pred) transits Han (7,7) then delivers at Chu (8,8).
            Setup("s1", quyuan, quyuan);
            Enter(han);
            Expect("C2 carry QuYuan at Han(7,7): no reject / no delivery", NoEvent() && Carry() == "s1");
            Leave();
            Enter(chu);
            Expect("C2 carry QuYuan at Chu(8,8): accepted", LastAccepted("s1") && Carry() == null && _pending.Count == 0);

            // Case 3: misrecognition - truth ZhaoJun predicted QuYuan.
            Setup("s2", zhaojun, quyuan);
            Enter(han);
            Expect("C3 mispredicted at Han(7,7) (not predicted dest): transit only", NoEvent() && Carry() == "s2");
            Leave();
            Enter(chu);
            Expect("C3 mispredicted at Chu(8,8): delivery_rejected queued",
                LastRejected("s2") && PendingReject("s2", chu) && Carry() == "s2");
            var before = _events.Count;
            Enter(chu);
            Expect("C3 re-entering same cell before leaving: no duplicate reject", _events.Count == before && _pending.Count == 1);
            _pending.Clear();
            Leave();
            FindStation("s2").PredictedRole = zhaojun; // backend correction
            Enter(han);
            Expect("C3 after backend correction at Han(7,7): accepted", LastAccepted("s2") && Carry() == null);

            // Case 4: misrecognition - truth QuYuan predicted ZhaoJun.
            Setup("s1", quyuan, zhaojun);
            Enter(chu);
            Expect("C4 mispredicted at Chu(8,8) (not predicted dest): transit only", NoEvent() && Carry() == "s1");
            Leave();
            Enter(han);
            Expect("C4 mispredicted at Han(7,7): delivery_rejected queued",
                LastRejected("s1") && PendingReject("s1", han) && Carry() == "s1");
            _pending.Clear();
            Leave();
            FindStation("s1").PredictedRole = quyuan;
            Enter(chu);
            Expect("C4 after backend correction at Chu(8,8): accepted", LastAccepted("s1") && Carry() == null);

            // Case 5: empty-handed at destinations: nothing.
            Setup(null, zhaojun, zhaojun);
            Enter(chu);
            Enter(han);
            Expect("C5 empty-handed at both destinations: nothing", NoEvent());

            Debug.Log("[DeliveryTest] --------------------------------------------------");
            Debug.Log(string.Format("[DeliveryTest] RESULT: {0} passed, {1} failed", _passed, _failed));
            if (_failed == 0)
                Debug.Log("[DeliveryTest] ALL DELIVERY-GATING ASSERTIONS PASSED");
            else
                Debug.LogError("[DeliveryTest] DELIVERY-GATING TEST FAILED");
            Quit(_failed == 0 ? 0 : 1);
        }

        private IEnumerator RunLiveEndToEndTest(bool expectLive)
        {
            var tag = expectLive ? "[LiveE2E]" : "[MockE2E]";
            Debug.Log(tag + " ==================================================");
            Debug.Log(tag + (expectLive ? " STARTING FULL 3-ACT LIVE API END-TO-END TEST" : " STARTING FULL 3-ACT MOCK END-TO-END TEST"));
            Debug.Log(tag + " ==================================================");
            Time.timeScale = 10f;

            PlannerTaskDriver driver = null;
            RobotController robot = null;
            PlannerHttpClient client = null;
            var timeout = Time.realtimeSinceStartup + 15f;
            while ((_sm = FindFirstObjectByType<StateMachineTrigger>()) == null ||
                   (driver = FindFirstObjectByType<PlannerTaskDriver>()) == null ||
                   (robot = FindFirstObjectByType<RobotController>()) == null ||
                   (client = FindFirstObjectByType<PlannerHttpClient>()) == null ||
                   !_sm.IsReady)
            {
                if (_sm != null && !string.IsNullOrEmpty(_sm.ErrorMessage))
                {
                    Debug.LogError(tag + " StateMachineTrigger init error: " + _sm.ErrorMessage);
                    Quit(1);
                    yield break;
                }
                if (Time.realtimeSinceStartup > timeout)
                {
                    Debug.LogError(tag + " Timed out waiting for scene initialization.");
                    Quit(1);
                    yield break;
                }
                yield return null;
            }

            var expectedMode = expectLive ? PlannerClientMode.Live : PlannerClientMode.Mock;
            if (client.Mode != expectedMode)
            {
                Debug.LogError(tag + " Expected " + expectedMode + " mode, but got " + client.Mode);
                Quit(1);
                yield break;
            }

            var rejections = 0;
            var deliveries = 0;
            var switches = 0;
            _sm.RecognitionCompleted += s =>
                Debug.Log(string.Format("{0} Recognized {1} at ({2},{3}) -> pred_role={4} ({5:P1}) [step={6}]",
                    tag, s.SlotId, s.RecognitionCell.x, s.RecognitionCell.y, s.PredictedRole, s.Confidence, robot.TotalStepCount));
            _sm.SlotSwitched += sw =>
            {
                switches++;
                Debug.Log(string.Format("{0} Switched carry {1} -> {2} at ({3},{4}) [step={5}]",
                    tag, sw.PreviousCarrySlot, sw.NewCarrySlot, sw.Cell.x, sw.Cell.y, robot.TotalStepCount));
            };
            _sm.DeliveryValidated += d =>
            {
                if (d.Accepted) deliveries++; else rejections++;
                Debug.Log(string.Format("{0} DeliveryValidated slot={1} cell=({2},{3}) accepted={4} [step={5}] msg={6}",
                    tag, d.SlotId, d.Cell.x, d.Cell.y, d.Accepted, robot.TotalStepCount, d.Message ?? "accepted"));
            };
            driver.ActChanged += act =>
                Debug.Log(string.Format("{0} ActChanged -> {1} (Act1 steps={2})", tag, act, robot.TotalStepCount));

            var runDeadline = Time.realtimeSinceStartup + 180f;
            while (driver.CurrentAct != DemoAct.Completed)
            {
                robot.TrySetSpeedMultiplier(200f);
                if (!string.IsNullOrEmpty(_sm.ErrorMessage))
                {
                    Debug.LogError(tag + " FAIL: StateMachine error at step " + robot.TotalStepCount + ": " + _sm.ErrorMessage);
                    Quit(1);
                    yield break;
                }
                if (driver.IsStopped && driver.CurrentAct == DemoAct.Act1_OnlineMission)
                {
                    // Wait briefly in case RunPostCompletionWorkflow coroutine is transitioning to Act 2
                    yield return new WaitForSecondsRealtime(0.2f);
                    if (driver.CurrentAct == DemoAct.Act1_OnlineMission && !string.IsNullOrEmpty(_sm.ErrorMessage))
                    {
                        Debug.LogError(tag + " FAIL: Driver stopped in Act 1: " + _sm.StatusMessage);
                        Quit(1);
                        yield break;
                    }
                }
                if (Time.realtimeSinceStartup > runDeadline)
                {
                    Debug.LogError(tag + " FAIL: Timed out at act=" + driver.CurrentAct + " step=" + robot.TotalStepCount);
                    Quit(1);
                    yield break;
                }
                yield return null;
            }

            var act3Valid = _sm.ValidateAllStationsDelivered(out var act3FailReason);
            Debug.Log(string.Format(
                "{0} SUMMARY: Act1Steps={1} (Forward={2}, Left={3}, Right={4}) | Deliveries={5} | Rejections={6} | Switches={7} | Act3Steps={8} | Act3Valid={9}",
                tag, robot.TotalStepCount, robot.ForwardStepCount, robot.LeftTurnStepCount, robot.RightTurnStepCount,
                deliveries, rejections, switches, robot.ReplayStepCount, act3Valid));

            if (!act3Valid || deliveries != 2)
            {
                Debug.LogError(tag + " FAIL: " + (act3FailReason ?? ("deliveries=" + deliveries)));
                Quit(1);
                yield break;
            }

            Debug.Log(tag + " PASS: All 3 Acts completed in " + expectedMode + " mode!");
            Quit(0);
        }

        private void Setup(string carry, string truth, string predicted)
        {
            _events.Clear();
            _pending.Clear();
            _rejectedCell.SetValue(_sm, null);
            var slot = carry ?? "s2";
            _truthRoles["s1"] = ProtocolConstants.Roles.QuYuan;
            _truthRoles["s2"] = ProtocolConstants.Roles.ZhaoJun;
            _truthRoles[slot] = truth;
            InitStation("s1", MazeLayout.ZhaoJunDestination, ProtocolConstants.Roles.QuYuan);   // QuYuan starts at (7,7)
            InitStation("s2", MazeLayout.QuYuanDestination, ProtocolConstants.Roles.ZhaoJun);   // ZhaoJun starts at (8,8)
            FindStation(slot).PredictedRole = predicted;
            if (carry != null)
                FindStation(carry).State = ProtocolConstants.SlotStates.Carried;
            _carrySlot.SetValue(_sm, carry);
        }

        private void InitStation(string slot, Vector2Int cell, string predicted)
        {
            var s = FindStation(slot);
            s.Cell = cell;
            s.IsDiscovered = true;
            s.IsRecognized = true;
            s.IsVerified = false;
            s.PredictedRole = predicted;
            s.State = ProtocolConstants.SlotStates.Waiting;
        }

        private StationRuntimeState FindStation(string slot)
        {
            foreach (var s in _sm.Stations)
                if (s.SlotId == slot)
                    return s;
            throw new InvalidOperationException("Missing station " + slot);
        }

        private void Enter(Vector2Int cell) { _validate.Invoke(_sm, new object[] { cell }); }
        private void Leave() { _rejectedCell.SetValue(_sm, null); }
        private string Carry() { return (string)_carrySlot.GetValue(_sm); }
        private bool NoEvent() { return _events.Count == 0 && _pending.Count == 0; }

        private bool LastAccepted(string slot)
        {
            return _events.Count > 0 && _events[_events.Count - 1].Accepted && _events[_events.Count - 1].SlotId == slot &&
                   FindStation(slot).State == ProtocolConstants.SlotStates.Delivered;
        }

        private bool LastRejected(string slot)
        {
            return _events.Count > 0 && !_events[_events.Count - 1].Accepted && _events[_events.Count - 1].SlotId == slot;
        }

        private bool PendingReject(string slot, Vector2Int cell)
        {
            if (_pending.Count != 1)
                return false;
            var e = _pending.Peek();
            return e.type == ProtocolConstants.EventTypes.DeliveryRejected && e.slot_id == slot &&
                   e.rejected_at != null && e.rejected_at.x == cell.x && e.rejected_at.z == cell.y;
        }

        private void Expect(string name, bool ok)
        {
            if (ok) _passed++; else _failed++;
            var line = "[DeliveryTest] " + (ok ? "PASS" : "FAIL") + " | " + name;
            if (ok) Debug.Log(line); else Debug.LogError(line);
        }

        private static void Quit(int code)
        {
            if (Application.isBatchMode)
                Application.Quit(code);
        }
    }
}
