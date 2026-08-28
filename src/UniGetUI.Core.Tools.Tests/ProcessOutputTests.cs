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
