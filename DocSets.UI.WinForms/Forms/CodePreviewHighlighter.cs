using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DocSets
{
    internal static class CodePreviewHighlighter
    {
        private const int WmSetRedraw = 0x000B;

        public static void Apply(RichTextBox box, string code, string filePath)
        {
            if (box == null)
            {
                return;
            }

            code = code ?? string.Empty;
            var selectionStart = box.SelectionStart;
            var selectionLength = box.SelectionLength;

            SendMessage(box.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            try
            {
                box.Text = code;
                box.SelectAll();
                box.SelectionColor = box.ForeColor;
                box.SelectionFont = box.Font;

                // RichTextBox normalizes line endings internally. Roslyn spans must be
                // calculated against the exact text stored by the control; otherwise every
                // CRLF before a token shifts its color range by one character.
                var displayedText = box.Text;
                if (IsCSharp(filePath) && displayedText.Length > 0)
                {
                    HighlightCSharp(box, displayedText);
                }

                box.Select(
                    Math.Min(selectionStart, box.TextLength),
                    Math.Min(selectionLength, Math.Max(0, box.TextLength - Math.Min(selectionStart, box.TextLength))));
            }
            finally
            {
                SendMessage(box.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                box.Invalidate();
            }
        }

        private static bool IsCSharp(string filePath)
        {
            return string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);
        }

        private static void HighlightCSharp(RichTextBox box, string code)
        {
            var dark = box.BackColor.GetBrightness() < 0.45f;
            var keywordColor = dark ? Color.FromArgb(86, 156, 214) : Color.Blue;
            var stringColor = dark ? Color.FromArgb(214, 157, 133) : Color.FromArgb(163, 21, 21);
            var commentColor = dark ? Color.FromArgb(106, 153, 85) : Color.Green;
            var numberColor = dark ? Color.FromArgb(181, 206, 168) : Color.FromArgb(9, 134, 88);
            var preprocessorColor = dark ? Color.FromArgb(155, 155, 155) : Color.Gray;

            var root = CSharpSyntaxTree.ParseText(code).GetRoot();
            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            {
                if (SyntaxFacts.IsKeywordKind(token.Kind()) || SyntaxFacts.IsContextualKeyword(token.Kind()))
                {
                    SetColor(box, token.Span.Start, token.Span.Length, keywordColor);
                }
                else if (token.IsKind(SyntaxKind.StringLiteralToken) ||
                         token.IsKind(SyntaxKind.CharacterLiteralToken) ||
                         token.IsKind(SyntaxKind.InterpolatedStringTextToken) ||
                         token.IsKind(SyntaxKind.Utf8StringLiteralToken) ||
                         token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) ||
                         token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
                {
                    SetColor(box, token.Span.Start, token.Span.Length, stringColor);
                }
                else if (token.IsKind(SyntaxKind.NumericLiteralToken))
                {
                    SetColor(box, token.Span.Start, token.Span.Length, numberColor);
                }

                HighlightTrivia(box, token.LeadingTrivia, commentColor, preprocessorColor);
                HighlightTrivia(box, token.TrailingTrivia, commentColor, preprocessorColor);
            }
        }

        private static void HighlightTrivia(RichTextBox box, SyntaxTriviaList triviaList, Color commentColor, Color preprocessorColor)
        {
            foreach (var trivia in triviaList)
            {
                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
                    trivia.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia))
                {
                    SetColor(box, trivia.Span.Start, trivia.Span.Length, commentColor);
                }
                else if (trivia.IsDirective)
                {
                    SetColor(box, trivia.FullSpan.Start, trivia.FullSpan.Length, preprocessorColor);
                }
            }
        }

        private static void SetColor(RichTextBox box, int start, int length, Color color)
        {
            if (start < 0 || length <= 0 || start >= box.TextLength)
            {
                return;
            }

            length = Math.Min(length, box.TextLength - start);
            box.Select(start, length);
            box.SelectionColor = color;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
