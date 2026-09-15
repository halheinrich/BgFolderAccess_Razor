using System.Reflection;
using System.Text.Json.Serialization;
using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The declarations that make this library's half of the trim gate a gate
/// rather than a suggestion (halheinrich/backgammon#197, in the mould of
/// XgFilter_Razor's posture pins for halheinrich/backgammon#193). The
/// analyzer's own verdict is enforced by the build under
/// TreatWarningsAsErrors; what these pin is that nobody quietly switches the
/// premises off — flipping IsTrimmable out of the csproj, moving the wire
/// context off metadata-only generation, or adding a wire DTO the context
/// does not reach — fails a test rather than silently deferring the next
/// trim-unsafe construct to BgQuiz's trimmed publish.
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

    // Metadata-only generation, the repo-wide rule every context declares: a
    // default-mode fast-path handler binds resolution to the context's own
    // private options. Pinned here so the one context this library owns keeps
    // the same declaration as every other link in the repo.
    [Fact]
    public void TheWireContext_GeneratesMetadataOnly()
    {
        var options = typeof(BgFolderAccessJsonContext)
            .GetCustomAttribute<JsonSourceGenerationOptionsAttribute>();

        Assert.NotNull(options);
        Assert.Equal(JsonSourceGenerationMode.Metadata, options!.GenerationMode);
    }

    // The guard against the regression the arc's proof found: a wire DTO that
    // crosses interop as InvokeAsync<T>'s type argument is kept through a trim
    // only as far as the annotation reaches, and the annotation does not reach
    // a record nested inside it (JsPickedFile lost its constructor that way).
    // The rule is that every reply is read through the context instead, so
    // every wire DTO must be resolvable through it — which is what this pins,
    // over every internal type nested in JsFolderAccess (the DTOs are the only
    // ones; the compiler's async state machines are private). A DTO added
    // without being reached from one of the context's roots fails here.
    //
    // Stated limit: this cannot see a DTO that is BOTH in the context and
    // passed to interop again. That half is caught at runtime by BgQuiz's
    // trimmed e2e (EnvironmentFidelityTests), the gate this repo's build
    // cannot replace — see INSTRUCTIONS.md, Test posture.
    [Fact]
    public void EveryWireDto_IsResolvableThroughTheWireContext()
    {
        var wireDtos = typeof(JsFolderAccess)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Where(t => t.IsNestedAssembly)
            .ToArray();

        Assert.NotEmpty(wireDtos); // an empty set would pass the loop vacuously
        Assert.All(
            wireDtos,
            dto => Assert.NotNull(BgFolderAccessJsonContext.Default.GetTypeInfo(dto)));
    }
}
