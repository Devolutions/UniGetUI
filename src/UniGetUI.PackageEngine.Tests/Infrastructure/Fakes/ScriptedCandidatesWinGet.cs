#if WINDOWS
using UniGetUI.PackageEngine.Managers.WingetManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class ScriptedCandidatesWinGet : WinGet
{
    private readonly IReadOnlyList<string> _candidates;

    public ScriptedCandidatesWinGet(IReadOnlyList<string> candidates)
    {
        _candidates = candidates;
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles() => _candidates;
}
#endif
