using System.Reflection;
using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The declaration that makes this library's half of the trim gate a gate
/// rather than a suggestion (halheinrich/backgammon#197, in the mould of
/// XgFilter_Razor's posture pins for halheinrich/backgammon#193). The
/// analyzer's own verdict is enforced by the build under
/// TreatWarningsAsErrors; what this pins is that nobody quietly switches the
/// premise off — flipping IsTrimmable out of the csproj fails a test rather
/// than silently deferring the next trim-unsafe construct to BgQuiz's
/// trimmed publish.
/// </summary>
public class BgFolderAccessRazorTrimPostureTests
{
    // IsTrimmable surfaces in the built assembly as SDK-emitted metadata,
    // which is the one trace of the csproj setting a test can read. The
    // analyzer switch beside it leaves no such trace; it is exercised instead
    // by the build itself, which fails on a reflection-bound serializer call.
    [Fact]
    public void TheAssembly_DeclaresItselfTrimmable()
    {
        Assert.Contains(
            typeof(JsFolderAccess).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            a => a.Key == "IsTrimmable" && a.Value == "True");
    }
}
