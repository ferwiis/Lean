/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Python.Runtime;

using QuantConnect.Python;
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Python
{
    [TestFixture]
    public class PythonInitializerTests
    {
        private const string ChildEnvironmentVariable = "LEAN_PYTHON_LIFECYCLE_TEST_CHILD";
        private const string CompletionMarker = "PYTHON_LIFECYCLE_SHUTDOWN_COMPLETE";

        [Test]
        public void ShutdownCompletesWithoutLeakingGil()
        {
            if (Environment.GetEnvironmentVariable(ChildEnvironmentVariable) == "shutdown")
            {
                using (Py.GIL())
                {
                    using var result = PythonEngine.Eval("6 * 7");
                    Assert.AreEqual(42, result.As<int>());
                }

                // Exercise the worker-thread shutdown path used by algorithms.
                var isolator = new Isolator();
                Assert.IsTrue(isolator.ExecuteWithTimeLimit(TimeSpan.FromSeconds(30), PythonInitializer.Shutdown, 5000));
                Assert.IsFalse(PythonEngine.IsInitialized);
                PythonInitializer.Shutdown();

                // Test-only pressure: expose leaked tokens instead of allowing a
                // successful child exit to hide a pending throwing finalizer.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                TestContext.Progress.WriteLine(CompletionMarker);
                return;
            }

            var child = RunLifecycleChild("shutdown");
            Assert.AreEqual(0, child.ExitCode, child.Output);
            StringAssert.Contains(CompletionMarker, child.Output);
            StringAssert.DoesNotContain("Py.GILState.Finalize", child.Output);
        }

        [Test]
        public void InvalidRuntimeFailsBeforeAcquiringGil()
        {
            var child = RunLifecycleChild("invalid-runtime");
            Assert.AreNotEqual(0, child.ExitCode, child.Output);
            StringAssert.Contains("DllNotFoundException", child.Output);
            StringAssert.Contains(nameof(AssemblyInitialize.InitializePythonForDiscovery), child.Output);
            StringAssert.DoesNotContain("Py.GILState.Finalize", child.Output);
            StringAssert.DoesNotContain(CompletionMarker, child.Output);
        }

        private static (int ExitCode, string Output) RunLifecycleChild(string mode)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("vstest");
            startInfo.ArgumentList.Add(typeof(PythonInitializerTests).Assembly.Location);
            startInfo.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + typeof(PythonInitializerTests).FullName + "." + nameof(ShutdownCompletesWithoutLeakingGil));
            startInfo.ArgumentList.Add("/Logger:console;verbosity=detailed");
            startInfo.Environment[ChildEnvironmentVariable] = mode;
            if (mode == "invalid-runtime")
            {
                startInfo.Environment["PYTHONNET_PYDLL"] = Path.Combine(Path.GetTempPath(), "lean-missing-python-" + Guid.NewGuid().ToString("N") + ".dll");
            }

            using var process = Process.Start(startInfo);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Python lifecycle child did not exit within two minutes.");
            }
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }

        [Test]
        public void AlgorithmLocationIsAlwaysBeforeOtherPaths()
        {
            PythonInitializer.Initialize();
            PythonInitializer.ResetAlgorithmLocationPath();

            var testDirectory = Directory.CreateDirectory("TestDir").FullName.Replace('\\', '/');
            var algorithmDirectory = Directory.CreateDirectory("AlgoDir").FullName.Replace('\\', '/');

            PythonInitializer.AddAlgorithmLocationPath(algorithmDirectory);
            PythonInitializer.AddPythonPaths(new string[] { testDirectory });
            
            var paths = GetPythonPaths().ToList();

            Directory.Delete("TestDir", true);
            Directory.Delete("AlgoDir", true);

            var algorithmDirectoryIndex = paths.IndexOf(algorithmDirectory);
            var testDirectoryIndex = paths.IndexOf(testDirectory);

            Assert.AreNotEqual(-1, algorithmDirectoryIndex, string.Join(", ", paths));
            Assert.Less(algorithmDirectoryIndex, testDirectoryIndex);
        }

        private static IEnumerable<string> GetPythonPaths()
        {
            using (Py.GIL())
            {
                using dynamic sys = Py.Import("sys");
                using var locals = new PyDict();
                locals.SetItem("sys", sys);

                // Filter out any already paths that already exist on our current PythonPath
                using var pythonCurrentPath = PythonEngine.Eval("sys.path", locals: locals);

                return pythonCurrentPath.As<List<string>>();
            }
        }
    }
}
