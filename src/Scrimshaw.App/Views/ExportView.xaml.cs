using System.Windows.Controls;

namespace Scrimshaw.App.Views;

/// <summary>The Export PDF tab.</summary>
public partial class ExportView : UserControl
{
    public ExportView()
    {
        InitializeComponent();
        // Thumbnails may have been regenerated since the last visit (replaced frames).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) PagePreview.ClearCache();
        };
    }
}
