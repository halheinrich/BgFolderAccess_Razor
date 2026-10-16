using System.Text.Json.Serialization;

namespace BgFolderAccess_Razor;

/// <summary>
/// The source-generated <see cref="JsonSerializerContext"/> for the module's structured
/// replies — <see cref="JsFolderAccess.JsPickStart"/>, <see cref="JsFolderAccess.JsCollected"/>
/// and <see cref="JsFolderAccess.JsDraw"/>, with the records they carry. Every reply crosses
/// interop as a <see cref="System.Text.Json.JsonElement"/> and is read through this context,
/// never as <c>InvokeAsync&lt;T&gt;</c>'s type argument: that annotation keeps <c>T</c> through a
/// trim but not the records nested in it, and BgQuiz's trimmed publish lost a nested
/// record's constructor that way (halheinrich/backgammon#197). CamelCase is the module's
/// key spelling, read strictly since this library ships the module. Metadata-only
/// generation is the repo's rule for every context; <c>BgFolderAccessRazorTrimPostureTests</c>
/// pins both.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsFolderAccess.JsPickStart))]
[JsonSerializable(typeof(JsFolderAccess.JsCollected))]
[JsonSerializable(typeof(JsFolderAccess.JsDraw))]
internal sealed partial class BgFolderAccessJsonContext : JsonSerializerContext
{
}
