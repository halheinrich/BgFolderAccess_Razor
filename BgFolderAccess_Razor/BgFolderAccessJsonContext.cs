using System.Text.Json.Serialization;

namespace BgFolderAccess_Razor;

/// <summary>
/// The source-generated <see cref="JsonSerializerContext"/> for the values
/// this library deserializes itself: the JS module's three replies —
/// <see cref="JsFolderAccess.JsPickStart"/>, <see cref="JsFolderAccess.JsEnumerateResult"/>
/// and <see cref="JsFolderAccess.JsFallbackResult"/> — with the arrays and
/// nested records they carry following from those three roots. Trim-safe
/// <c>System.Text.Json</c> metadata produced at compile time instead of by
/// runtime reflection — the mould of XgFilter_Razor's
/// <c>XgFilterRazorJsonContext</c> (halheinrich/backgammon#193), applied here
/// in halheinrich/backgammon#197 after BgQuiz's trimmed publish lost
/// <see cref="JsFolderAccess.JsPickedFile"/>'s constructor.
///
/// <para>
/// <b>Why a context at all, when the JS runtime deserializes for free.</b>
/// <c>IJSObjectReference.InvokeAsync&lt;TValue&gt;</c> annotates
/// <c>TValue</c> with <c>DynamicallyAccessedMembers</c> (public constructors,
/// fields and properties), which keeps the type named as the type argument
/// through a trim. The annotation is not transitive: a record reached only as
/// the element type of an annotated record's array property is not named
/// anywhere the trimmer looks, and BgQuiz's partial trim removed its
/// constructor — the reflection-based deserializer the runtime uses then
/// fails at the first file with <c>DeserializeNoConstructor</c>. The trim
/// analyzer cannot see it either: the call it inspects is annotated. So no
/// library type crosses interop as a type argument. Each reply comes back as
/// a <see cref="System.Text.Json.JsonElement"/>, a framework type the runtime
/// reads without touching this assembly, and is deserialized here, where the
/// generator has already named every type in the graph.
/// </para>
///
/// <para>
/// <b>What is declared, and why.</b> The three reply roots only. Everything
/// else that crosses interop is a framework type — <see cref="bool"/>,
/// <see cref="string"/>, <c>IJSObjectReference</c>, <c>IJSStreamReference</c>
/// — which the runtime's own converters and annotations cover; the arguments
/// this library sends (the caps table, an element reference) are serialized
/// by the runtime and are not this context's business.
/// </para>
///
/// <para>
/// <b>CamelCase, stated.</b> The module's replies are plain JavaScript
/// objects with camelCase keys, which the JS runtime used to read
/// case-insensitively under its own camelCase policy. The policy is declared
/// here so that a value serialized through this context is the wire shape —
/// the bUnit tests script replies exactly that way — and read strictly: this
/// library ships the module, so the key spelling is a fact, not a tolerance.
/// </para>
///
/// <para>
/// <b>Internal, deliberately.</b> Nothing here is a type anyone else sees:
/// the replies exist between the shipped module and <see cref="JsFolderAccess"/>,
/// and a consumer that could resolve their metadata would be a consumer that
/// knew the wire, which the DTOs' own <c>internal</c> posture exists to
/// prevent. The test project reaches it through the same
/// <c>InternalsVisibleTo</c> the DTOs have.
/// </para>
///
/// <para>
/// <b>Metadata-only generation</b>, the arc's binding rule: a default-mode
/// fast-path handler would bind resolution to this context's private options,
/// and declaring the same mode as every other context in the repo means
/// nobody has to re-derive whether that matters for this one. Pinned by
/// <c>BgFolderAccessRazorTrimPostureTests</c>, beside the pin that every
/// wire DTO resolves through this context.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsFolderAccess.JsPickStart))]
[JsonSerializable(typeof(JsFolderAccess.JsEnumerateResult))]
[JsonSerializable(typeof(JsFolderAccess.JsFallbackResult))]
internal sealed partial class BgFolderAccessJsonContext : JsonSerializerContext
{
}
