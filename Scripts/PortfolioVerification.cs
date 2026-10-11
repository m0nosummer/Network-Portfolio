using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Arena.EditorTools
{
    public static class PortfolioVerification
    {
        public static void VerifyAndBuildBatch()
        {
            RunBatch();
            BuildWindowsBatch();
        }

        public static void BuildWindowsBatch()
        {
            string output = Environment.GetEnvironmentVariable("ARENA_BUILD_PATH");
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("ARENA_BUILD_PATH is required");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Windows build failed: {report.summary.result}");
            Debug.Log("[PortfolioBuild] PASS Windows64");
        }

        [MenuItem("Tools/Verification/Portfolio Preparation")]
        public static void RunBatch()
        {
            DependencyInjectionVerification.RunBatch();
            PredictionReplayVerification.RunBatch();
            NetworkLifecycleVerification.RunBatch();
            Debug.Log("[PortfolioVerification] PASS suites=3");
        }
    }
}
