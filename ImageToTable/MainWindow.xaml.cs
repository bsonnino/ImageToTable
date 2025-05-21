using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Microsoft.Windows.AI.Imaging;

namespace ImageToTable
{

    public class RecognizedCell(string Text, Point TopLeft, Point BottomRight)
    {
        public string Text { get; } = Text;
        public Point TopLeft { get; } = TopLeft;
        public Point BottomRight { get; } = BottomRight;
        public int Top => (int)TopLeft.Y;
        public int Left => (int)TopLeft.X;
        public int Bottom => (int)BottomRight.Y;
        public int Right => (int)BottomRight.X;
        public int Row { get; private set; }
        public int Column { get; private set; }
        public void SetRow(int row) => Row = row;
        public void SetColumn(int column) => Column = column;
        public override string ToString() => $"({Text}, {TopLeft}, {BottomRight}, R: {Row}  C: {Column})";
    }


    public sealed partial class MainWindow : Window
    {
        private TextRecognizer? _textRecognizer;

        public MainWindow()
        {
            this.InitializeComponent();
            MainGrid.Loaded += (s, e) => InitializeRecognizer();
        }

        private async void PasteImage_Click(object sender, RoutedEventArgs e)
        {
            var package = Clipboard.GetContent();
            if (!package.Contains(StandardDataFormats.Bitmap))
            {
                StatusText.Text = "Clipboard does not contain an image";
                return;
            }
            StatusText.Text = string.Empty;
            var streamRef = await package.GetBitmapAsync();

            IRandomAccessStream stream = await streamRef.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            var bitmap = await decoder.GetSoftwareBitmapAsync();
            var source = new SoftwareBitmapSource();

            SoftwareBitmap displayableImage = SoftwareBitmap.Convert(bitmap,
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            await source.SetBitmapAsync(displayableImage);
            ImageSrc.Source = source;
            RecognizeAndAddTable(displayableImage);
        }

        public async void InitializeRecognizer()
        {
            try
            {
                SetButtonEnabled(false);
                var readyState = TextRecognizer.GetReadyState();
                if (readyState is AIFeatureReadyState.NotSupportedOnCurrentSystem or AIFeatureReadyState.DisabledByUser)
                {
                    StatusText.Text = "OCR not available in this system";
                    return;
                }
                if (readyState == AIFeatureReadyState.NotReady)
                {
                    StatusText.Text = "Installing OCR";
                    var installTask = TextRecognizer.EnsureReadyAsync();

                    installTask.Progress = (installResult, progress) => DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusText.Text = $"Progress: {progress * 100:F1}";
                    });

                    var result = await installTask;
                    StatusText.Text = "Done: " + result.Status.ToString();
                }
                _textRecognizer = await TextRecognizer.CreateAsync();
                SetButtonEnabled(true);
            }
            catch (Exception ex)
            {
                ContentDialog dialog = new ContentDialog
                {
                    Title = "Error initializing OCR",
                    CloseButtonText = "Ok",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = ex.Message
                };

                dialog.XamlRoot = this.Content.XamlRoot;
                await dialog.ShowAsync();
            }
        }

        private void SetButtonEnabled(bool isEnabled)
        {
            PasteButton.IsEnabled = isEnabled;
        }

        public void RecognizeAndAddTable(SoftwareBitmap bitmap)
        {
            if (_textRecognizer == null)
            {
                StatusText.Text = "OCR not initialized";
                return;
            }
            var imageBuffer = ImageBuffer.CreateForSoftwareBitmap(bitmap);
            var result = _textRecognizer.RecognizeTextFromImage(imageBuffer);
            if (result.Lines == null || result.Lines.Length == 0)
            {
                StatusText.Text = "No text found";
                return;
            }

            var cells = result.Lines.Select(l => 
                new RecognizedCell(l.Text, l.BoundingBox.TopLeft, l.BoundingBox.BottomRight)).ToList();

            var maxRow = SetRows(cells);
            SetCols(cells);

            var table = CreateTable(cells, maxRow);
            TableText.Text = table;
        }

        private static string CreateTable(List<RecognizedCell> cells, int maxRow)
        {
            var columnWidths = cells.GroupBy(b => b.Column).OrderBy(g => g.Key)
                .Select(g => g.Max(b => b.Text.Length)).ToArray();
            var tableTop = columnWidths.Aggregate(string.Empty, 
                (current, width) => current + $"+{new string('-', width + 2)}") + '+' + 
                    Environment.NewLine;
            var headerColumns = cells.Where(b => b.Row == 1).ToArray();
            var tableHeader = GetLine(headerColumns, columnWidths);
            var table = tableTop + tableHeader + tableTop;
            for (var i = 1; i < maxRow; i++)
            {
                var columns = cells.Where(b => b.Row == i + 1).ToArray();
                table += GetLine(columns, columnWidths);
            }
            table += tableTop;
            return table;
        }

        private static void SetCols(List<RecognizedCell> cells)
        {
            var sortedByCols = cells.OrderBy(r => r.Left).ThenBy(r => r.Top);
            var currentX = 0.0;
            var currCol = 0;
            foreach (var box in sortedByCols)
            {
                if (box.Left > currentX)
                {
                    currCol++;
                    box.SetColumn(currCol);
                    currentX = Math.Max(box.Right, currentX);
                }
                else
                {
                    box.SetColumn(currCol);
                }
            }
        }

        private static int SetRows(List<RecognizedCell> cells)
        {
            var sortedByRows = cells.OrderBy(r => r.Top).ThenBy(r => r.Left);
            var currentY = 0.0;
            var currRow = 0;
            foreach (var box in sortedByRows)
            {
                if (box.Top > currentY)
                {
                    currRow++;
                    box.SetRow(currRow);
                    currentY = Math.Max(box.Bottom, currentY);
                }
                else
                {
                    box.SetRow(currRow);
                }
            }

            return currRow;
        }

        private static string GetLine(RecognizedCell[] columns, int[] columnWidths)
        {
            if (columns.Length == 0)
            {
                return string.Empty;
            }
            var line = string.Empty;
            for (var j = 0; j < columnWidths.Length; j++)
            {
                var column = columns.FirstOrDefault(c => c.Column == j + 1);

                line += column == null ? "| " + new string(' ', columnWidths[j]) + " " :
                    "| " + column.Text.PadRight(columnWidths[j]) + " ";
            }
            line += '|' + Environment.NewLine;
            return line;
        }
    }
}

