using System.Text.Json;
using PdfReader.Core.Annotations;
using PdfReader.Core.Json;
using Path = System.IO.Path;

namespace PdfReader.Services;

/// <summary>Saved signatures (the vector strokes, see <see cref="SavedSignature"/>), kept across documents
/// in one small JSON file.</summary>
public sealed class SignatureStore
{
	private readonly string _file;
	private List<SavedSignature>? _items;

	public SignatureStore(string appDataDirectory) => _file = Path.Combine(appDataDirectory, "signatures.json");

	public IReadOnlyList<SavedSignature> List() => Load();

	public void Add(SavedSignature signature)
	{
		List<SavedSignature> items = Load();
		items.Insert(0, signature);
		if (items.Count > 12) items.RemoveRange(12, items.Count - 12);
		Save(items);
	}

	public void Delete(Guid id)
	{
		List<SavedSignature> items = Load();
		items.RemoveAll(s => s.Id == id);
		Save(items);
	}

	private List<SavedSignature> Load()
	{
		if (_items != null) return _items;
		try { _items = File.Exists(_file) ? JsonSerializer.Deserialize(File.ReadAllText(_file), CoreJsonContext.Default.ListSavedSignature) ?? [] : []; }
		catch { _items = []; }
		return _items;
	}

	private void Save(List<SavedSignature> items)
	{
		_items = items;
		File.WriteAllText(_file, JsonSerializer.Serialize(items, CoreJsonContext.Default.ListSavedSignature));
	}
}
