using System.Diagnostics;

namespace UniGetUI.Core.Tools.Tests
{
    public class ProcessOutputTests
    {
        [Fact]
        public void TryReadStandardOutput_ReturnsTheCommandOutput()
        {
            ProcessStartInfo startInfo = OperatingSystem.IsWindows()
                ? Redirected("cmd.exe", "/c", "echo", "unigetui")
                : Redirected("/bin/sh", "-c", "echo unigetui");

            Assert.True(CoreTools.TryReadStandardOutput(startInfo, TimeSpan.FromSeconds(30), out string output));
            Assert.Equal("unigetui", output);
        }

        [Fact]
        public void TryReadStandardOutput_GivesUpOnAChildThatNeverExits()
        {
            // #5236: a child holding stdout open (a login shell stuck in a recursive `exec zsh -l`)
            // used to block ReadToEnd() forever, so the timeout was never reached.
            ProcessStartInfo startInfo = OperatingSystem.IsWindows()
                // A child that stays alive and silent holds stdout open exactly like the stuck
                // login shell did. Sleeping avoids depending on the network stack or on stdin.
                ? Redirected("powershell.exe", "-NoProfile", "-Command", "Start-Sleep", "-Seconds", "60")
                : Redirected("/bin/sh", "-c", "sleep 60");

            var watch = Stopwatch.StartNew();
            bool succeeded = CoreTools.TryReadStandardOutput(startInfo, TimeSpan.FromSeconds(2), out string output);
            watch.Stop();

            Assert.False(succeeded);
            Assert.Equal("", output);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Gave up after {watch.Elapsed}");
        }

        [Fact]
        public void TryReadStandardOutput_KeepsTheTotalWaitWithinTheTimeout()
        {
            // A child that drops stdout late but never exits used to cost a second full timeout
            // waiting for the exit, so the advertised budget nearly doubled.
            // Unix-only: neither cmd nor PowerShell can release their stdout handle mid-script.
            if (!OperatingSystem.IsWindows())
            {
                ProcessStartInfo startInfo =
                    Redirected("/bin/sh", "-c", "sleep 3; echo late; exec 1>&-; sleep 60");

                var watch = Stopwatch.StartNew();
                bool succeeded =
                    CoreTools.TryReadStandardOutput(startInfo, TimeSpan.FromSeconds(4), out string output);
                watch.Stop();

                Assert.True(succeeded);
                Assert.Equal("late", output);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5.5), $"Took {watch.Elapsed}");
            }
        }

        [Fact]
        public void TryReadStandardOutput_StillGivesUpWhenADescendantHoldsTheOutputOpen()
        {
            // Worst case: the command exits at once but leaves a background process holding stdout,
            // so no EOF ever arrives and the reparented descendant is beyond Kill's reach. The
            // budget must still be honoured -- losing the output beats hanging on the splash.
            // Unix-only: cmd and PowerShell cannot detach a child onto the same stdout handle.
            if (!OperatingSystem.IsWindows())
            {
                ProcessStartInfo startInfo = Redirected("/bin/sh", "-c", "echo orphaned; sleep 5 &");

                var watch = Stopwatch.StartNew();
                bool succeeded =
                    CoreTools.TryReadStandardOutput(startInfo, TimeSpan.FromSeconds(2), out string output);
                watch.Stop();

                Assert.False(succeeded);
                Assert.Equal("", output);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Took {watch.Elapsed}");
            }
        }

        [Fact]
        public void TryReadStandardOutput_ReturnsFalseWhenTheCommandDoesNotExist()
        {
            Assert.False(CoreTools.TryReadStandardOutput(
                Redirected("unigetui-this-command-does-not-exist"), TimeSpan.FromSeconds(30), out string output));
            Assert.Equal("", output);
        }

        private static ProcessStartInfo Redirected(string fileName, params string[] args)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);
            return startInfo;
        }
    }
}
