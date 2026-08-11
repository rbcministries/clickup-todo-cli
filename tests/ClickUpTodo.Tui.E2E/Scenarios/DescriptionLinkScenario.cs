namespace ClickUpTodo.Tui.E2E;

/// <summary>
/// Seeds extra links into the task Description for the link-rendering checks — a seed, so every other check
/// sees the body byte-for-byte:
/// <list type="bullet">
/// <item><b>#430 (E2E_MD_LINK=1)</b> — appends a markdown <c>[text](url)</c> link whose visible text is prose
/// ("the runbook") and whose resolved target differs from it, so the OSC-8 check can assert the hyperlink
/// points at the RESOLVED url, not the visible text.</item>
/// <item><b>#443 (E2E_WRAP_SPLIT=1)</b> — appends two links positioned to be SPLIT by word wrap at a narrow
/// COLS: a bare URL longer than the pane's inner width (hard-wrapped mid-URL) and a markdown link whose
/// visible text wraps across rows.</item>
/// </list>
/// Both mutate the shared <see cref="FakeClickUp.Description"/> before any request is served; they never
/// co-occur in a check, but both append independently if set, preserving the original composition.
/// </summary>
internal sealed class DescriptionLinkScenario : IE2EScenario
{
    public const string MdLinkTarget = "https://example.com/runbook-42";
    public const string WrapSplitUrl = "https://ex.io/wrap/aa/bb/cc/dd/ee/ff/gg/hh/ii/jj/ENDURL";
    public const string WrapSplitMdTarget = "https://ex.io/rb42";
    public const string WrapSplitMdVisible = "the operations runbook and deployment procedure ENDVIS";

    private static bool MdLink => Environment.GetEnvironmentVariable("E2E_MD_LINK") == "1";
    private static bool WrapSplit => Environment.GetEnvironmentVariable("E2E_WRAP_SPLIT") == "1";

    public string Name => "description-links";
    public bool IsActive => MdLink || WrapSplit;

    public void SeedBackend(FakeClickUp backend)
    {
        if (MdLink)
            backend.Description += "\n\nSee [the runbook](" + MdLinkTarget + ") for steps";
        if (WrapSplit)
            backend.Description += "\n\nSplit URL: " + WrapSplitUrl + " done\n\nMD: ["
                                   + WrapSplitMdVisible + "](" + WrapSplitMdTarget + ") fin";
    }
}
