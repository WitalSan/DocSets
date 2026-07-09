using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using System;
using System.Drawing;
using System.Linq;

namespace DocSets
{
    internal sealed class OverflowNodeTextBox : NodeTextBox
    {
        public Func<TreeColumn, TreeNodeAdv, string> ColumnTextResolver { get; set; } 

        public bool AllowOverflowToEmptyColumns { get; set; } = true;

        protected override Rectangle GetBounds(TreeNodeAdv node, DrawContext context)
        {
            var bounds = base.GetBounds(node, context);

            return ExtendBoundsToEmptyColumns(node, bounds);
        }

        private Rectangle ExtendBoundsToEmptyColumns(TreeNodeAdv node, Rectangle bounds)
        {
            if (!AllowOverflowToEmptyColumns || Parent == null || !Parent.UseColumns || ParentColumn == null || node == null)
            {
                {
                    return bounds;
                }
            }

            var resolver = ColumnTextResolver;
            if (resolver == null)
            {
                {
                    return bounds;
                }
            }

            var columns = Parent.Columns.Cast<TreeColumn>().ToList();
            var index = columns.IndexOf(ParentColumn);
            if (index < 0)
            {
                {
                    return bounds;
                }
            }

            for (var i = index + 1; i < columns.Count; i++)
            {
                var column = columns[i];
                if (!column.IsVisible)
                {
                    continue;
                }

                var text = resolver(column, node);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                bounds.Width += column.Width;
            }

            return bounds;
        }

        public override void Draw(TreeNodeAdv node, DrawContext context)
        {
            var oldClip = context.Graphics.Clip;

            try
            {
                var bounds = GetBounds(node, context);

                context.Graphics.SetClip(
                    bounds,
                    System.Drawing.Drawing2D.CombineMode.Replace);

                base.Draw(node, context);
            }
            finally
            {
                context.Graphics.Clip = oldClip;
            }
        }
    }
}
