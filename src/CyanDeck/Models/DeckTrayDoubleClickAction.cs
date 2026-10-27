namespace CyanDeck.Models;

// v8.0.44: what a notification-area icon double-click does. QuickHide is the
// pre-v8.0.44 behavior and the default for settings files without the key.
public enum DeckTrayDoubleClickAction { QuickHide, OpenSettings, None }
