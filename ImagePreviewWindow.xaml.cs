using System.Windows;
using System.Windows.Media.Imaging;

namespace DeskLink
{
    public partial class ImagePreviewWindow : Window
    {
        internal ImagePreviewWindow(BitmapSource image, string peer)
        {
            InitializeComponent();
            Preview.Source = image;
            Recipient.Text = "发送给 " + peer + "  ·  " + image.PixelWidth + " × " + image.PixelHeight;
        }

        private void Send(object sender, RoutedEventArgs e) { DialogResult = true; }
    }
}
