namespace Yaesu_Web_Control;

/// <summary>
/// Size limits for the pages that post a whole table in one form.
///
/// ASP.NET Core caps a form at 1024 name/value pairs by default
/// (<c>FormOptions.ValueCountLimit</c>). That is a sensible guard on a public
/// server; here it is a hard ceiling on how many memory channels the operator
/// may own, because the Memories page posts every row on every Save.
///
/// Going over it does not produce an error message. The antiforgery filter is
/// the first thing to read the form, so the framework reports
/// "Antiforgery token validation failed ... Unable to read the antiforgery
/// request token from the posted form", with
/// <c>InvalidDataException: Form value count limit 1024 exceeded</c> buried
/// underneath, and the browser shows a bare HTTP 400 with no page at all.
/// That is indistinguishable, on screen and in the log, from an expired
/// antiforgery token on a page left open too long.
///
/// Bruce VK2RT hit it on #167 (2026-09-19): he imported channels 001-099 from
/// his FTdx101D, edited one label, pressed Save, and got HTTP ERROR 400. At 20
/// posted fields per row the page breaks at 52 memories, so every operator who
/// imports a reasonably full radio is locked out of editing memories entirely.
/// </summary>
public static class WebFormLimits
{
    /// <summary>
    /// Form fields each memory row contributes. Eighteen names, two of which
    /// (RxClarOn, TxClarOn) post twice — a checkbox plus the hidden "false"
    /// that makes an unticked box bind. MemoriesFormSizeTests counts the real
    /// markup against this, so adding a column can't quietly lower the ceiling.
    /// </summary>
    public const int MemoryRowFields = 20;

    /// <summary>
    /// Memories the page must handle. A radio holds 99 channels; the ADIF
    /// import creates one per unique frequency/mode pair in a log, which is
    /// where a big number would come from.
    /// </summary>
    public const int SupportedMemoryRows = 1_000;

    /// <summary>
    /// Form value ceiling. The slack covers the antiforgery token and any
    /// other field on the same page.
    /// </summary>
    public const int ValueCountLimit = (MemoryRowFields * SupportedMemoryRows) + 1_024;

    /// <summary>
    /// Bound-collection ceiling (<c>MvcOptions.MaxModelBindingCollectionSize</c>,
    /// also 1024 by default). Kept above the number of rows
    /// <see cref="ValueCountLimit"/> can admit, so the form limit is always
    /// what stops an absurd post — exceeding this one throws, and the operator
    /// would get a bare 500 instead.
    /// </summary>
    public const int ModelBindingCollectionSize = 2_000;
}
