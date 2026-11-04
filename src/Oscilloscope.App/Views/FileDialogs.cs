using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oscilloscope.App.Views;

internal static class FileDialogs
{
    private static readonly FilePickerFileType Csv = new("CSV file")
    {
        Patterns = new[] { "*.csv" },
        MimeTypes = new[] { "text/csv" },
    };

    /// <summary>Asks for a file name and writes the text there. Returns the file name, or null when cancelled.</summary>
    public static async Task<string?> SaveTextAsync(TopLevel owner, string title, string suggestedName, string text)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { Csv },
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return null;

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
            stream.SetLength(0); // some providers do not truncate an existing file

        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text);
        return file.Name;
    }
}
