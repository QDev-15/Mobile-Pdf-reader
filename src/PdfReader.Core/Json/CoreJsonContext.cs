using System.Text.Json.Serialization;
using PdfReader.Core.Annotations;
using PdfReader.Core.Text;

namespace PdfReader.Core.Json;

/// <summary>
/// Source-generated JSON for everything the app saves to disk (annotations, saved signatures, OCR text).
/// Generated at build time rather than found by reflection at run time, because the Release build trims
/// unused-looking code and reflection-based JSON then silently loses data (the record constructors get
/// trimmed away); source generation keeps exactly what is needed.
/// </summary>
[JsonSerializable(typeof(List<Annotation>))]
[JsonSerializable(typeof(List<SavedSignature>))]
[JsonSerializable(typeof(Dictionary<int, List<TextWord>>))]
public partial class CoreJsonContext : JsonSerializerContext
{
}
