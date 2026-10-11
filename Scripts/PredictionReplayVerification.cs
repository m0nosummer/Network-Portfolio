using System;
using System.Collections.Generic;
using Arena.Network;
using Arena.Player.Prediction;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    public static class PredictionReplayVerification
    {
        private const float Epsilon = 0.00001f;
        private const string ConfigPath = "Assets/_SOs/NetConfig.asset";

        [MenuItem("Tools/Verification/Prediction Replay")]
        public static void RunFromMenu()
        {
            Debug.Log(RunAll());
        }

        public static void RunBatch()
        {
            Debug.Log(RunAll());
        }

        private static string RunAll()
        {
            VerifyAssertions();
            var config = AssetDatabase.LoadAssetAtPath<NetworkConfig>(ConfigPath);
            if (config == null)
                throw new InvalidOperationException($"NetworkConfig not found at {ConfigPath}");

            var passedCases = new List<string>();
            var failures = new List<string>();

            RunCase("configured timing baseline", passedCases, failures, () =>
            {
                Require(config.ClientTickRateHz == 20,
                    $"client tick: expected 20, actual {config.ClientTickRateHz}");
                Require(config.ServerTickRateHz == 20,
                    $"server tick: expected 20, actual {config.ServerTickRateHz}");
                Near(config.PlayerMoveSpeed, 7f, "move speed");
                Near(Time.fixedDeltaTime, 0.02f, "fixed delta time");
            });

            float sendInterval = 1f / config.ClientTickRateHz;
            float nominalIntervalDistance = config.PlayerMoveSpeed * sendInterval;
            float legacyOneStepDistance = config.PlayerMoveSpeed * Time.fixedDeltaTime;
            float mismatch = nominalIntervalDistance - legacyOneStepDistance;

            RunCase("legacy unit mismatch", passedCases, failures, () =>
            {
                Near(nominalIntervalDistance, 0.35f, "20 Hz nominal interval distance");
                Near(legacyOneStepDistance, 0.14f, "one 50 Hz fixed-step distance");
                Near(mismatch, 0.21f, "distance mismatch");
                Near(legacyOneStepDistance / nominalIntervalDistance, 0.4f, "legacy ratio");
            });

            RunCase("mixed fixed-step displacement", passedCases, failures, () =>
            {
                var history = new ClientPredictionHistory(8);
                history.AccumulatePredictedDisplacement(new Vector3(0.14f, 0f, 0f));
                history.AccumulatePredictedDisplacement(new Vector3(0.14f, 0f, 0f));
                history.AccumulatePredictedDisplacement(new Vector3(-0.14f, 0f, 0f));
                history.RecordSentInput(1);

                VectorNear(history.GetReplayOffset(), new Vector3(0.14f, 0f, 0f),
                    "mixed-direction replay offset");
                Require(history.PendingCount == 1,
                    $"pending count: expected 1, actual {history.PendingCount}");
            });

            RunCase("acknowledged segment removal", passedCases, failures, () =>
            {
                var history = CreateAckScenario();
                history.Acknowledge(1);

                VectorNear(history.GetReplayOffset(), new Vector3(-0.28f, 0f, 0f),
                    "offset after acknowledging sequence 1");
                Require(history.PendingCount == 1,
                    $"pending count: expected 1, actual {history.PendingCount}");
            });

            RunCase("unsent tail preservation", passedCases, failures, () =>
            {
                var history = CreateAckScenario();
                history.Acknowledge(2);

                VectorNear(history.GetReplayOffset(), new Vector3(-0.14f, 0f, 0f),
                    "offset after acknowledging all sent segments");
                Require(history.PendingCount == 0,
                    $"pending count: expected 0, actual {history.PendingCount}");
            });

            RunCase("stale acknowledgement rejection", passedCases, failures, () =>
            {
                var history = CreateAckScenario();
                Require(history.Acknowledge(2), "latest acknowledgement was rejected");
                Vector3 offsetBeforeStaleAck = history.GetReplayOffset();

                Require(!history.Acknowledge(1), "older acknowledgement was accepted");
                VectorNear(history.GetReplayOffset(), offsetBeforeStaleAck,
                    "offset after stale acknowledgement");
            });

            RunCase("out-of-order snapshot rejection", passedCases, failures, () =>
            {
                var history = new ClientPredictionHistory(4);
                Require(history.TryAcceptSnapshot(10), "first snapshot was rejected");
                Require(history.TryAcceptSnapshot(12), "newer snapshot was rejected");
                Require(!history.TryAcceptSnapshot(11), "older snapshot was accepted");
                Require(!history.TryAcceptSnapshot(12), "duplicate snapshot was accepted");

                history.Clear();
                Require(history.TryAcceptSnapshot(uint.MaxValue),
                    "pre-wrap snapshot was rejected");
                Require(history.TryAcceptSnapshot(0),
                    "wrapped newer snapshot was rejected");
            });

            RunCase("vertical displacement exclusion", passedCases, failures, () =>
            {
                var history = new ClientPredictionHistory(4);
                history.AccumulatePredictedDisplacement(new Vector3(1f, 99f, 2f));
                VectorNear(history.GetReplayOffset(), new Vector3(1f, 0f, 2f),
                    "horizontal-only replay offset");
            });

            RunCase("overflow and clear policy", passedCases, failures, () =>
            {
                var history = new ClientPredictionHistory(2);
                history.RecordSentInput(1);
                history.RecordSentInput(2);
                history.RecordSentInput(3);

                Require(history.PendingCount == 2,
                    $"bounded pending count: expected 2, actual {history.PendingCount}");
                Require(history.HistoryOverflowed, "overflow flag was not set");

                history.Acknowledge(3);
                Require(history.HistoryOverflowed,
                    "acknowledgement must not hide a previous history overflow");

                history.Clear();
                Require(history.PendingCount == 0, "clear did not remove pending segments");
                Require(!history.HistoryOverflowed, "clear did not reset overflow flag");
                VectorNear(history.GetReplayOffset(), Vector3.zero, "offset after clear");
            });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[PredictionReplayVerification] FAIL cases={failures.Count}\n" +
                    string.Join("\n", failures));
            }

            return
                $"[PredictionReplayVerification] PASS cases={passedCases.Count} " +
                $"unity={Application.unityVersion} fixedDt={Time.fixedDeltaTime:F5} " +
                $"sendDt={sendInterval:F5} speed={config.PlayerMoveSpeed:F2} " +
                $"legacy={legacyOneStepDistance:F2} nominal={nominalIntervalDistance:F2} " +
                $"mismatch={mismatch:F2}";
        }

        private static ClientPredictionHistory CreateAckScenario()
        {
            var history = new ClientPredictionHistory(8);
            history.AccumulatePredictedDisplacement(new Vector3(0.42f, 0f, 0f));
            history.RecordSentInput(1);
            history.AccumulatePredictedDisplacement(new Vector3(-0.14f, 0f, 0f));
            history.RecordSentInput(2);
            history.AccumulatePredictedDisplacement(new Vector3(-0.14f, 0f, 0f));
            return history;
        }

        private static void RunCase(
            string name,
            ICollection<string> passedCases,
            ICollection<string> failures,
            Action test)
        {
            try
            {
                test();
                passedCases.Add(name);
            }
            catch (Exception ex)
            {
                failures.Add($"- {name}: {ex.Message}");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static void Near(float actual, float expected, string label)
        {
            if (!(Mathf.Abs(actual - expected) <= Epsilon))
            {
                throw new InvalidOperationException(
                    $"{label}: expected {expected:F6}, actual {actual:F6}");
            }
        }

        private static void VectorNear(Vector3 actual, Vector3 expected, string label)
        {
            if (!((actual - expected).sqrMagnitude <= Epsilon * Epsilon))
            {
                throw new InvalidOperationException(
                    $"{label}: expected {expected}, actual {actual}");
            }
        }

        private static void VerifyAssertions()
        {
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Arena.Verification.VerificationChecks.MustReject(() => Near(invalid, 0, "scalar"), "non-finite scalar");
                Arena.Verification.VerificationChecks.MustReject(() => Near(invalid, invalid, "scalar"), "non-finite expected scalar");
                Arena.Verification.VerificationChecks.MustReject(
                    () => VectorNear(new Vector3(0, invalid, 0), Vector3.zero, "vector"), "non-finite vector");
            }
            var history = CreateAckScenario();
            history.Acknowledge(1);
            history.Clear();
            Arena.Verification.VerificationChecks.MustReject(
                () => VectorNear(history.GetReplayOffset(), new Vector3(-0.28f, 0, 0), "ACK replay"),
                "unacknowledged history removed");
            Debug.Log("[PredictionOracle] PASS non-finite rejection and lost-history negative control");
        }
    }
}
