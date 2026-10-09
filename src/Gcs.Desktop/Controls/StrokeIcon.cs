using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Gcs.Desktop.Controls;

/// <summary>
/// A line icon: a geometry drawn in a 24 × 24 box with round caps, scaled to the control's size and stroked with the
/// inherited text colour, so an icon inside a button follows the button's normal, hover and disabled colours.
/// The geometries live in <c>Themes/Icons.axaml</c>.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    /// <summary>In the 24-unit design box: 2 gives about 1.3 px at the usual 16 px.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    private const double DesignSize = 24;

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, StrokeThicknessProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<StrokeIcon>(16);
        HeightProperty.OverrideDefaultValue<StrokeIcon>(16);
        FocusableProperty.OverrideDefaultValue<StrokeIcon>(false);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / DesignSize;
        var offset = new Vector((Bounds.Width - (DesignSize * scale)) / 2, (Bounds.Height - (DesignSize * scale)) / 2);
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            context.DrawGeometry(null, pen, data);
        }
    }
}
