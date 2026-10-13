namespace CyanDeck.Appearance;

internal static class BackgroundColorAdjustmentIntegrationContract
{
    internal const string ManagedCapability = "Appearance.Edit";

    internal const string StateRule =
        "Do not add persisted Hue/Saturation/Brightness/Transparency fields. Existing per-group ARGB background color remains the single durable source of truth.";

    internal const string SliderRule =
        "Expose Hue 0..360 degrees and Saturation/Brightness/Transparency 0..100 percent. Transparency 0 means opaque and 100 means fully transparent.";

    internal const string ColorRule =
        "CyanDeck v7.26 maps Brightness to the standard HSV value component as an independent implementation choice; do not claim Stardock's internal color math is identical.";

    internal const string ResetRule =
        "Reset reads the full host's default background color for the selected group and changes background color/transparency only; it must not reset title font, corners, tabs, icon tint, Roll-up, or other appearance settings.";

    internal const string PersistenceRule =
        "Apply through CyanDeck's existing appearance command and debounced state writer. The overlay itself must not write layout.json or create a sidecar file.";

    internal const string ChameleonRule =
        "Edit the saved base background appearance. v7.22 Chameleon full-color/blended presentation remains transient and must not write back sampled/presentation colors.";

    internal const string CopyPasteRule =
        "Because the existing ARGB background property is updated, v1.4 appearance copy/paste/reset and templates/snapshots keep using their existing ownership without a parallel color state.";
}
