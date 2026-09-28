namespace UniGetUI.AgentSkills.Cli;

/// <summary>Runs the skills CLI within the current process without starting the desktop UI.</summary>
public static class SkillsCli
{
    /// <summary>Runs a command and returns its exit code without terminating the host process.</summary>
    public static Task<int> RunAsync(string[] args) => Task.Run(() => Skills.Program.RunCommand(args));
}
