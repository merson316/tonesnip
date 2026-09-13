using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class AnnotationDocTests
{
    private static BoxShape Box(int id, int x = 0) => new(id, new IntRect(x, 0, 10, 10), 2, 1, false, false);

    [Fact]
    public void Add_replace_remove_are_undoable_in_order()
    {
        var d = new AnnotationDoc();
        d.Add(Box(d.NewId()));
        d.Add(Box(d.NewId(), 20));
        d.Replace(Box(1, 5));
        d.Remove(2);
        Assert.Single(d.Shapes); Assert.Equal(5, d.Shapes[0].Bounds.Left + 2);
        Assert.True(d.Undo()); Assert.Equal(2, d.Shapes.Count);
        Assert.True(d.Undo()); Assert.Equal(0, ((BoxShape)d.Shapes[0]).Rect.Left);
        Assert.True(d.Undo()); Assert.Single(d.Shapes);
        Assert.True(d.Undo()); Assert.Empty(d.Shapes);
        Assert.False(d.Undo());
        Assert.True(d.Redo()); Assert.Single(d.Shapes);
        Assert.True(d.CanRedo);
        d.Add(Box(d.NewId(), 40));
        Assert.False(d.CanRedo);   // new edit clears redo
    }

    [Fact]
    public void Undo_depth_is_fifty()
    {
        var d = new AnnotationDoc();
        for (int i = 0; i < 60; i++) d.Add(Box(d.NewId(), i));
        int undone = 0; while (d.Undo()) undone++;
        Assert.Equal(50, undone);
        Assert.Equal(10, d.Shapes.Count);
    }

    [Fact]
    public void HasEdits_survives_the_undo_cap()
    {
        var d = new AnnotationDoc();
        for (int i = 0; i < 60; i++) d.Add(Box(d.NewId(), i));   // more edits than the undo depth
        d.MarkSaved();
        Assert.False(d.HasEdits);
        d.Add(Box(d.NewId(), 999));
        Assert.True(d.HasEdits);   // a comparison of undo depth would saturate at the cap
        Assert.True(d.Undo());
        Assert.False(d.HasEdits);
    }

    [Fact]
    public void Ids_from_an_imported_document_are_renumbered_and_stay_distinct()
    {
        // The viewer imports the overlay's shapes, which carry the overlay document's ids; re-stamping them with
        // NewId keeps the next drawn shape from colliding with one of them.
        var foreign = new Shape[] { Box(1), Box(2, 20) };
        var d = new AnnotationDoc();
        foreach (Shape s in foreign) d.Add(s with { Id = d.NewId() });
        d.Add(Box(d.NewId(), 40));
        Assert.Equal(3, d.Shapes.Count);
        Assert.Equal(3, d.Shapes.Select(s => s.Id).Distinct().Count());
        d.Remove(3);
        Assert.Equal(2, d.Shapes.Count);
        Assert.Equal(new[] { 1, 2 }, d.Shapes.Select(s => s.Id));
    }

    [Fact]
    public void Crop_is_an_undoable_edit_that_leaves_shapes_alone()
    {
        var d = new AnnotationDoc();
        d.Add(Box(d.NewId(), 30));
        d.SetCrop(new IntRect(20, 0, 50, 50));
        Assert.Equal(new IntRect(20, 0, 50, 50), d.Crop);
        Assert.Equal(30, ((BoxShape)d.Shapes[0]).Rect.Left);
        Assert.True(d.Undo()); Assert.True(d.Crop.IsEmpty);
        Assert.True(d.Redo()); Assert.False(d.Crop.IsEmpty);
    }

    [Fact]
    public void HitTop_returns_the_topmost_shape_and_selection_follows_removal()
    {
        var d = new AnnotationDoc();
        d.Add(Box(d.NewId()));
        d.Add(Box(d.NewId(), 5));
        Assert.Equal(2, d.HitTop(6, 0)!.Id);
        Assert.Equal(1, d.HitTop(0, 0)!.Id);
        Assert.Null(d.HitTop(50, 50));
        d.Select(2); Assert.Equal(2, d.Selected!.Id);
        d.Remove(2); Assert.Null(d.SelectedId);
    }

    [Fact]
    public void Changed_reports_the_union_of_old_and_new_bounds()
    {
        var d = new AnnotationDoc();
        IntRect last = IntRect.Empty;
        d.Changed += r => last = r;
        d.Add(Box(d.NewId()));
        Assert.Equal(Box(1).Bounds, last);
        d.Replace(Box(1, 100));
        Assert.Equal(Box(1).Bounds.Union(Box(1, 100).Bounds), last);
        d.Undo();
        Assert.Equal(IntRect.Empty, last);   // undo/redo: everything
    }

    [Fact]
    public void Counter_numbers_increase_and_exposure_zebra_are_not_edits()
    {
        var d = new AnnotationDoc();
        Assert.Equal(1, d.NextCounter); d.Add(new CounterShape(d.NewId(), 0, 0, d.NextCounter, 20, 1)); Assert.Equal(2, d.NextCounter);
        d.Exposure = 2f; d.Zebra = true;
        Assert.True(d.HasEdits);
        d.Undo();
        Assert.Equal(2f, d.Exposure); Assert.True(d.Zebra);
        Assert.False(d.HasEdits);
        d.Redo(); d.MarkSaved();
        Assert.False(d.HasEdits);
        d.Add(new CounterShape(d.NewId(), 5, 5, d.NextCounter, 20, 1));
        Assert.True(d.HasEdits);
    }

    [Fact]
    public void Translated_moves_shapes_and_crop_and_carries_state_without_touching_the_original()
    {
        var d = new AnnotationDoc { Private = false, Exposure = 1.5f };
        var redact = new RedactShape(d.NewId(), new IntRect(5, 5, 10, 10), 3, Blur: false, Private: true, Seed: 99);
        d.Add(redact);
        var box = Box(d.NewId(), 20);
        d.Add(box);
        d.SetCrop(new IntRect(1, 2, 30, 40));

        AnnotationDoc t = d.Translated(100, 200);

        Assert.Equal(2, t.Shapes.Count);
        var tRedact = (RedactShape)t.Shapes[0];
        Assert.Equal(new IntRect(105, 205, 10, 10), tRedact.Rect);
        Assert.Equal(redact.Id, tRedact.Id);
        Assert.Equal(redact.Seed, tRedact.Seed);
        Assert.Equal(redact.Strength, tRedact.Strength);
        Assert.True(tRedact.Private);
        var tBox = (BoxShape)t.Shapes[1];
        Assert.Equal(120, tBox.Rect.Left);
        Assert.Equal(box.Id, tBox.Id);
        Assert.Equal(new IntRect(101, 202, 30, 40), t.Crop);
        Assert.False(t.Private);
        Assert.Equal(1.5f, t.Exposure);
        Assert.False(t.HasEdits);

        // Original document is unchanged.
        Assert.Equal(new IntRect(5, 5, 10, 10), redact.Rect);
        Assert.Equal(new IntRect(1, 2, 30, 40), d.Crop);
        Assert.Equal(20, ((BoxShape)d.Shapes[1]).Rect.Left);
    }

    [Fact]
    public void Translated_leaves_an_empty_crop_empty()
    {
        var d = new AnnotationDoc();
        d.Add(Box(d.NewId()));
        AnnotationDoc t = d.Translated(10, 10);
        Assert.True(t.Crop.IsEmpty);
    }

    [Fact]
    public void Redo_clears_a_selection_the_redone_edit_removes()
    {
        var d = new AnnotationDoc();
        d.Add(Box(d.NewId())); d.Add(Box(d.NewId(), 20));
        d.Remove(1);
        Assert.True(d.Undo());
        d.Select(1); Assert.Equal(1, d.SelectedId);
        Assert.True(d.Redo());
        Assert.Null(d.SelectedId); Assert.Single(d.Shapes);
    }
}

public class AnnotationDocSaveTests
{
    private static BoxShape Box(AnnotationDoc d, int x) => new(d.NewId(), new IntRect(x, 0, 10, 10), 2, 0xFFFF0000, false, false);

    [Fact]
    public void Marking_an_earlier_revision_saved_leaves_edits_made_since_it_unsaved()
    {
        // The editor's save renders, then awaits an encode, then marks the document saved. An edit made during the
        // encode is not in the file, so it must not be marked saved with the rest.
        var d = new AnnotationDoc();
        d.Add(Box(d, 0));
        long rendered = d.Revision;
        d.Add(Box(d, 20));                   // drawn while the encode was running
        d.MarkSaved(rendered);
        Assert.True(d.HasEdits);
        Assert.True(d.Undo());               // back to exactly what was written
        Assert.False(d.HasEdits);
    }
}
