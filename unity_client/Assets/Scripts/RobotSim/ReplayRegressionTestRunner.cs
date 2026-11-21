using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Automated regression test runner for Act 3 replay, switch_steps [4, 5],
    /// structured event auditing, and strict dual-figure delivery validation.
    /// Can be triggered via command-line argument: -runReplayTest
    /// Zero scene modification required (automatically bootstrapped when flag is present).
    /// </summary>
    public sealed class ReplayRegressionTestRunner : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRegisterIfCommandLineRequested()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-runReplayTest", StringComparison.OrdinalIgnoreCase))
                {
                    var go = new GameObject("ReplayRegressionTestRunner");
                    go.AddComponent<ReplayRegressionTestRunner>();
                    DontDestroyOnLoad(go);
                    break;
                }
            }
        }

        private void Start()
        {
            StartCoroutine(RunRegressionTestSuite());
        }

        public IEnumerator RunRegressionTestSuite()
        {
            Debug.Log("[ReplayRegressionTest] ==================================================");
            Debug.Log("[ReplayRegressionTest] STARTING ACT 3 REPLAY & SWITCH_STEPS REGRESSION TEST");
            Debug.Log("[ReplayRegressionTest] ==================================================");

            var taskDriver = FindFirstObjectByType<PlannerTaskDriver>();
            var stateMachine = FindFirstObjectByType<StateMachineTrigger>();
            var robot = FindFirstObjectByType<RobotController>();

            if (taskDriver == null || stateMachine == null || robot == null)
            {
                Debug.LogError("[ReplayRegressionTest] Required components not found in active scene.");
                QuitIfBatchMode(1);
                yield break;
            }

            // Wait briefly for scene components to be ready
            yield return new WaitForSeconds(0.4f);

            var fixturePath = Path.Combine(Application.streamingAssetsPath, "Mock", "sample_summary_switch_step4_5.json");
            if (!File.Exists(fixturePath))
            {
                Debug.LogError("[ReplayRegressionTest] Fixture missing: " + fixturePath);
                QuitIfBatchMode(1);
                yield break;
            }

            PlanSummaryResponseDto summary = null;
            try
            {
                var json = File.ReadAllText(fixturePath, Encoding.UTF8);
                summary = JsonConvert.DeserializeObject<PlanSummaryResponseDto>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ReplayRegressionTest] Failed to deserialize fixture: " + ex.Message);
                QuitIfBatchMode(1);
                yield break;
            }

            // Set high speed for fast test execution
            robot.TrySetSpeedMultiplier(50f);

            Debug.Log("[ReplayRegressionTest] Executing RunReplayWorkflow with switch_steps: [4, 5]...");
            yield return taskDriver.RunReplayWorkflow(summary);

            // Audit events validation
            var events = stateMachine.ReplayAuditEvents;
            Debug.Log("[ReplayRegressionTest] Total audit events recorded: " + events.Count);

            var switchStep4Found = false;
            var switchStep5Found = false;
            var pickupS1Found = false;
            var deliverS1Found = false;
            var pickupS2Found = false;
            var deliverS2Found = false;

            for (var i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                Debug.Log(string.Format("[ReplayRegressionTest] Event #{0}: {1} at Step {2} | Pose: ({3},{4},{5}°) | Details: {6}",
                    i + 1, evt.event_type.ToUpper(), evt.step_index,
                    evt.pose != null && evt.pose.cell != null ? evt.pose.cell[0] : -1,
                    evt.pose != null && evt.pose.cell != null ? evt.pose.cell[1] : -1,
                    evt.pose != null ? evt.pose.heading_degrees : 0,
                    evt.details));

                if (evt.event_type == "switch" && evt.step_index == 4 && evt.state != null && evt.state.is_success)
                    switchStep4Found = true;
                if (evt.event_type == "switch" && evt.step_index == 5 && evt.state != null && evt.state.is_success)
                    switchStep5Found = true;
                if (evt.event_type == "pickup" && evt.step_index == 1)
                    pickupS1Found = true;
                if (evt.event_type == "deliver" && evt.step_index == 42)
                    deliverS1Found = true;
                if (evt.event_type == "pickup" && evt.step_index == 81)
                    pickupS2Found = true;
                if (evt.event_type == "deliver" && evt.step_index == 125)
                    deliverS2Found = true;
            }

            // Strict Delivery Verification
            var allDelivered = stateMachine.ValidateAllStationsDelivered(out var failReason);

            Debug.Log("[ReplayRegressionTest] --------------------------------------------------");
            Debug.Log("[ReplayRegressionTest] VERIFICATION RESULTS:");
            Debug.Log("[ReplayRegressionTest] 1. Initial Pickup s1 (Step 1): " + (pickupS1Found ? "PASS" : "FAIL"));
            Debug.Log("[ReplayRegressionTest] 2. Switch before Step 4 (s1 -> s2): " + (switchStep4Found ? "PASS" : "FAIL"));
            Debug.Log("[ReplayRegressionTest] 3. Switch before Step 5 (s2 -> s1): " + (switchStep5Found ? "PASS" : "FAIL"));
            Debug.Log("[ReplayRegressionTest] 4. Deliver s1 to (8,8) (Step 42): " + (deliverS1Found ? "PASS" : "FAIL"));
            Debug.Log("[ReplayRegressionTest] 5. Pickup s2 at (2,0) (Step 81): " + (pickupS2Found ? "PASS" : "FAIL"));
            Debug.Log("[ReplayRegressionTest] 6. Deliver s2 to (7,7) (Step 125): " + (deliverS2Found ? "PASS" : "FAIL"));
            Debug.Log("[ReplayRegressionTest] 7. Strict Final Dual-Delivery Check: " + (allDelivered ? "PASS" : "FAIL: " + failReason));
            Debug.Log("[ReplayRegressionTest] --------------------------------------------------");

            var passed = pickupS1Found && switchStep4Found && switchStep5Found && deliverS1Found && pickupS2Found && deliverS2Found && allDelivered;

            if (passed)
            {
                Debug.Log("[ReplayRegressionTest] ✅ ALL 7 REGRESSION ASSERTIONS PASSED PERFECTLY!");
                QuitIfBatchMode(0);
            }
            else
            {
                Debug.LogError("[ReplayRegressionTest] ❌ REGRESSION TEST FAILED!");
                QuitIfBatchMode(1);
            }
        }

        private void QuitIfBatchMode(int exitCode)
        {
            if (Application.isBatchMode)
            {
                Application.Quit(exitCode);
            }
        }
    }
}
