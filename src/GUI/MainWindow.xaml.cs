using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace GUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 窗口尺寸上限跟着工作区走：这台机器 150% 缩放时逻辑工作区只有 1280x672，
            // 写死的高宽会比工作区还高，CenterScreen 居中后标题栏会被顶到屏幕外。
            // 在 Show 之前 clamp 成工作区大小，任何分辨率下窗口都完整可见。
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
        }
    }
}