using VideoFrameCut.Core;

var cases = new (string Name, Action Test)[]
{
    ("211 seconds / 25 second segments (CFR)", Test211Seconds),
    ("frame boundary snaps, no overlap and no omissions", TestFullCoverage),
    ("VFR uses actual PTS and not average FPS", TestVariableFrameRate),
    ("deleted gaps and manual cuts remain separate", TestDeletedGaps),
    ("export toggles and Undo/Redo", TestUndoRedo),
    ("invalid inputs and last frame", TestEdgeCases),
    ("CFR 24/30/60 exact boundaries", TestConstantFrameRates),
    ("clip-only preview uses exclusive end frame", TestClipPlaybackBounds),
    ("clip preview times use relative source PTS", TestClipPlaybackVfrOffset),
    ("three-second navigation respects source frames", TestSecondsNavigation),
    ("three-second navigation stays within selected clip", TestBoundedSecondsNavigation),
};
var failed = 0;
foreach (var test in cases)
{
    try
    {
        test.Test();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
        failed++;
    }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} passed");
return failed == 0 ? 0 : 1;

static void Assert(bool value, string reason)
{
    if (!value) throw new Exception(reason);
}

static void Test211Seconds()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(211 * 24, 24);
    var editor = new ClipEditor(index);
    Assert(editor.SplitByTime(25), "Expected a split.");
    var ranges = editor.Segments.Select(s => s.SourceRange).ToArray();
    Assert(ranges.Length == 9, $"Expected 9 segments; got {ranges.Length}");
    Assert(ranges.Take(8).All(r => r.Count == 600), "First 8 segments must be 600 frames.");
    Assert(ranges[^1].Count == 264, "Remaining 11 seconds must be 264 frames.");
}

static void TestFullCoverage()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(2997, 30000, 1001);
    var editor = new ClipEditor(index);
    Assert(editor.SplitByTime(10.1), "Expected a split.");
    var ranges = editor.Segments.Select(s => s.SourceRange).ToArray();
    Assert(ranges[0].Start == 0 && ranges[^1].End == index.FrameCount, "Missing first or last frame.");
    for (var i = 1; i < ranges.Length; i++)
        Assert(ranges[i - 1].End == ranges[i].Start, $"Gap or overlap at {i}");
    Assert(ranges.Sum(r => r.Count) == index.FrameCount, "Total frame count mismatch.");
}

static void TestVariableFrameRate()
{
    // Unequal spacing; target 0.1 is nearest boundary 0.07 (index 2), not 0.16.
    var index = new DisplayFrameIndex(new[] { 0.0, 0.03, 0.07, 0.16, 0.20, 0.27 }, 0.34);
    Assert(index.NearestBoundary(0.10, 0, 6) == 2, "VFR nearest PTS boundary incorrect.");
    var result = TimeSplitter.Split(index, new FrameRange(0, 6), 0.10);
    Assert(result.Count == 4, "Expected 4 non-empty VFR segments.");
    Assert(result[0].End == 2, "First VFR cut must be at frame 2.");
    Assert(result[^1].End == 6, "End boundary must include the last frame.");
    Assert(result.Sum(r => r.Count) == 6, "VFR frames lost.");
}

static void TestDeletedGaps()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(60 * 30, 30);
    var editor = new ClipEditor(index);
    Assert(editor.SplitAt(300), "Cut at 10 seconds.");
    Assert(editor.SplitAt(450), "Cut at 15 seconds.");
    var removed = editor.Segments.Single(s => s.SourceRange == new FrameRange(300, 450));
    Assert(editor.Delete(new[] { removed.Id }), "Delete 10-15 seconds.");
    editor.SplitByTime(10);
    Assert(editor.Segments.All(s => !s.SourceRange.Overlaps(new FrameRange(300, 450))), "Deleted area was reintroduced.");
    Assert(editor.Segments.All(s => !(s.SourceRange.Start < 450 && s.SourceRange.End > 300)), "Split crossed deleted gap.");
}

static void TestUndoRedo()
{
    var editor = new ClipEditor(DisplayFrameIndex.FromConstantFrameRate(900, 30));
    editor.SplitAt(300);
    var originalIds = editor.Segments.Select(s => s.Id).ToArray();
    editor.SetExportEnabled(new[] { originalIds[0] }, false);
    Assert(editor.ExportSelection().Count == 1, "Exclude toggle must filter exports.");
    Assert(editor.Undo(), "Undo expected.");
    Assert(editor.ExportSelection().Count == 2, "Undo should restore export state.");
    Assert(editor.Redo(), "Redo expected.");
    Assert(editor.ExportSelection().Count == 1, "Redo should reapply export state.");
    editor.SplitAt(600);
    Assert(!editor.CanRedo, "New command must invalidate redo.");
    editor.Delete(editor.Segments.Select(s => s.Id));
    Assert(editor.Segments.Count == 0, "All segments can be removed.");
    Assert(editor.Undo() && editor.Segments.Count == 3, "Undo deletion must restore all segments.");
}

static void TestEdgeCases()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(1, 25);
    var editor = new ClipEditor(index);
    Assert(!editor.SplitAt(0) && !editor.SplitAt(1), "Must not create empty segments.");
    Assert(!editor.SplitByTime(10), "One frame cannot split.");
    Assert(editor.Segments.Single().SourceRange.Count == 1, "Last frame lost.");
    Assert(index.NearestBoundary(index.EndTime, 0, 1) == 1, "Last boundary should be N.");
    try
    {
        _ = new DisplayFrameIndex(new[] { 0.0, 0.0 }, 0.1);
        throw new Exception("Duplicate PTS should fail.");
    }
    catch (ArgumentException) { }
}

static void TestConstantFrameRates()
{
    foreach (var fps in new[] { 24, 30, 60 })
    {
        var index = DisplayFrameIndex.FromConstantFrameRate(fps * 100, fps);
        var result = TimeSplitter.Split(index, new FrameRange(0, index.FrameCount), 25);
        Assert(result.Count == 4, $"Expected 4 segments at {fps} FPS.");
        Assert(result.All(r => r.Count == fps * 25), $"Wrong frames per clip at {fps} FPS.");
    }
}

static void TestClipPlaybackBounds()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(300, 30);
    var clip = new ClipPlaybackBounds(index, new FrameRange(30, 90));
    Assert(clip.FirstFrame == 30 && clip.LastFrame == 89, "Last frame must be end - 1.");
    Assert(Math.Abs(clip.StartSeconds - 1.0) < 1e-9, "Start time incorrect.");
    Assert(Math.Abs(clip.EndSeconds - 3.0) < 1e-9, "Exclusive end time incorrect.");
    Assert(clip.ClampFrame(0) == 30 && clip.ClampFrame(999) == 89, "Seeking escaped selected clip.");
    Assert(!clip.IsAtOrBeyondEnd(2.99) && clip.IsAtOrBeyondEnd(3.0), "Stop must trigger at clip end.");
}

static void TestClipPlaybackVfrOffset()
{
    var index = new DisplayFrameIndex(new[] { 10.00, 10.04, 10.12, 10.24, 10.30 }, 10.44);
    var clip = new ClipPlaybackBounds(index, new FrameRange(1, 4));
    Assert(Math.Abs(clip.StartSeconds - .04) < 1e-8, "Source PTS offset not removed.");
    Assert(Math.Abs(clip.EndSeconds - .30) < 1e-8, "Exclusive end for VFR must use next frame PTS.");
    Assert(clip.LastFrame == 3, "Last VFR frame incorrect.");
}

static void TestSecondsNavigation()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(900, 30);
    Assert(FrameNavigation.BySeconds(index, 300, 3) == 390, "3 sec forward at 30 fps = 90 frames.");
    Assert(FrameNavigation.BySeconds(index, 300, -3) == 210, "3 sec backward at 30 fps = 90 frames.");
    Assert(FrameNavigation.BySeconds(index, 0, -3) == 0, "Must clamp to first frame.");
    Assert(FrameNavigation.BySeconds(index, 899, 3) == 899, "Must clamp to last frame.");
}

static void TestBoundedSecondsNavigation()
{
    var index = DisplayFrameIndex.FromConstantFrameRate(600, 30);
    var region = new FrameRange(180, 270);
    Assert(FrameNavigation.BySeconds(index, 200, -3, region) == 180, "Seek must stop at clip start.");
    Assert(FrameNavigation.BySeconds(index, 255, 3, region) == 269, "Seek must stop at last clip frame.");
    var vfr = new DisplayFrameIndex(new[] { 10.0, 10.1, 10.6, 12.0, 13.2, 17.0 }, 18);
    Assert(FrameNavigation.BySeconds(vfr, 1, 2.0) == 3, "Use PTS nearest boundary, not average FPS.");
    Assert(FrameNavigation.BySeconds(vfr, 1, 2.0, new FrameRange(1, 3)) == 2,
        "VFR seek must not cross exclusive end.");
}
