using System.Text.Json;
using PdfReader.Core.Json;

namespace PdfReader.Core.Annotations;

/// <summary>
/// All annotations of one open PDF, with undo/redo. Each change replaces the list with a new snapshot
/// and keeps the previous one on the undo stack -- the annotation records are immutable, so a snapshot is
/// just a reference to a list. No UI, no Android: unit-tested on the PC.
/// </summary>
public sealed class AnnotationDocument
{
	private const int MaxHistory = 100;

	private IReadOnlyList<Annotation> _current = [];
	private readonly List<IReadOnlyList<Annotation>> _undo = [];
	private readonly List<IReadOnlyList<Annotation>> _redo = [];

	public IReadOnlyList<Annotation> Items => _current;

	public bool CanUndo => _undo.Count > 0;

	public bool CanRedo => _redo.Count > 0;

	/// <summary>Raised after every change (including undo/redo and live <see cref="Replace"/> while dragging).</summary>
	public event Action? Changed;

	public IEnumerable<Annotation> OnPage(int page) => _current.Where(a => a.Page == page);

	public IReadOnlySet<int> PagesWithAnnotations() => _current.Select(a => a.Page).ToHashSet();

	public void Add(Annotation annotation) => AddRange([annotation]);

	public void AddRange(IEnumerable<Annotation> annotations)
	{
		var added = annotations.ToList();
		if (added.Count == 0) return;
		Checkpoint();
		_current = _current.Concat(added).ToList();
		Changed?.Invoke();
	}

	public void Remove(Guid id)
	{
		if (_current.All(a => a.Id != id)) return;
		Checkpoint();
		_current = _current.Where(a => a.Id != id).ToList();
		Changed?.Invoke();
	}

	/// <summary>Swaps the annotation with the same <see cref="Annotation.Id"/>. Pass
	/// <paramref name="recordHistory"/> false for the many tiny steps of a drag, after one
	/// <see cref="Checkpoint"/> at its start, so the whole drag undoes as one step.</summary>
	public void Replace(Annotation updated, bool recordHistory = true)
	{
		int i = IndexOf(updated.Id);
		if (i < 0) return;
		if (recordHistory) Checkpoint();
		var next = _current.ToList();
		next[i] = updated;
		_current = next;
		Changed?.Invoke();
	}

	/// <summary>Remembers the current state as an undo step (and drops any redo history).</summary>
	public void Checkpoint()
	{
		_undo.Add(_current);
		if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
		_redo.Clear();
	}

	public void Clear()
	{
		if (_current.Count == 0) return;
		Checkpoint();
		_current = [];
		Changed?.Invoke();
	}

	public void Undo()
	{
		if (_undo.Count == 0) return;
		_redo.Add(_current);
		_current = _undo[^1];
		_undo.RemoveAt(_undo.Count - 1);
		Changed?.Invoke();
	}

	public void Redo()
	{
		if (_redo.Count == 0) return;
		_undo.Add(_current);
		_current = _redo[^1];
		_redo.RemoveAt(_redo.Count - 1);
		Changed?.Invoke();
	}

	public Annotation? Find(Guid id) => _current.FirstOrDefault(a => a.Id == id);

	/// <summary>The top-most annotation at (x, y) on <paramref name="page"/>, or null.</summary>
	public Annotation? HitTest(int page, float x, float y, float pageAspect, float tolerance = 0.015f)
	{
		for (int i = _current.Count - 1; i >= 0; i--)
		{
			Annotation a = _current[i];
			if (a.Page == page && a.HitTest(x, y, pageAspect, tolerance)) return a;
		}
		return null;
	}

	private int IndexOf(Guid id)
	{
		for (int i = 0; i < _current.Count; i++)
			if (_current[i].Id == id) return i;
		return -1;
	}

	public string ToJson() => JsonSerializer.Serialize(_current.ToList(), CoreJsonContext.Default.ListAnnotation);

	/// <summary>Restores a saved list (no history). A corrupt or older-shaped file yields an empty document
	/// rather than an exception.</summary>
	public static AnnotationDocument FromJson(string json)
	{
		var doc = new AnnotationDocument();
		try
		{
			List<Annotation>? items = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListAnnotation);
			if (items != null) doc._current = items;
		}
		catch (JsonException)
		{
		}
		return doc;
	}
}
