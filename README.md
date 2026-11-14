# Translator (.NET 5 Windows Forms)

A basic Windows desktop translation app built with standard WinForms controls only
(MenuStrip, ListBox, TextBox, Button, ToolTip) and no custom styling.

- The list shows words, sentences and paragraphs translated into the selected language.
- Hover over an item to see a tooltip with the **original text** and **from language -> to language**.
- Hover over **Translate** in the menu bar to see the 24 languages. Click one to translate the whole list into it.
- Type text in any language in the box at the bottom and click **Add**. Its language is
  detected automatically and it is translated into the current language.
- Select an item and click **Remove** (or press Delete) to remove it.

Languages: English, French, Spanish, German, Italian, Portuguese, Dutch, Russian,
Chinese (Simplified), Japanese, Korean, Arabic, Hindi, Tamil, Bengali, Turkish, Polish,
Swedish, Greek, Hebrew, Thai, Vietnamese, Indonesian, Ukrainian.

## Run

```
cd Translator
dotnet run
```

Or open `Translator/Translator.csproj` in Visual Studio and press F5.

The project targets `net5.0-windows`. `RollForward=Major` is set so it also runs on a newer
Windows Desktop runtime if the .NET 5 runtime is not installed.

## Notes

- Translation and detection use the public Google Translate endpoint (`sl=auto`), so an
  internet connection is required. It sits behind `ITranslationService`, so you can swap in another service.
- Items are kept in memory and reset when the app closes.
