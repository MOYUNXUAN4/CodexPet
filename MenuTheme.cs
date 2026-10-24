using System.Drawing;
using System.Windows.Forms;

namespace CodexPet;

// 右键菜单跟着卡片走深色，别突然弹一块系统白板
internal sealed class DarkMenuColors : ProfessionalColorTable
{
    private static readonly Color Bg = Color.FromArgb(0x16, 0x1B, 0x24);
    private static readonly Color Hot = Color.FromArgb(0x22, 0x2B, 0x38);
    private static readonly Color Edge = Color.FromArgb(0x2A, 0x33, 0x41);

    public override Color ToolStripDropDownBackground => Bg;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color MenuBorder => Edge;
    public override Color MenuItemBorder => Hot;
    public override Color MenuItemSelected => Hot;
    public override Color MenuItemSelectedGradientBegin => Hot;
    public override Color MenuItemSelectedGradientEnd => Hot;
    public override Color SeparatorDark => Edge;
    public override Color SeparatorLight => Edge;
}
