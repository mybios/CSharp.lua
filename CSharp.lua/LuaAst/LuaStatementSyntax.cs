/*
Copyright 2017 YANG Huan (sy.yanghuan@gmail.com).

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpLua.LuaAst {
  public abstract class LuaStatementSyntax : LuaSyntaxNode {
    public Semicolon SemicolonToken => Tokens.Semicolon;

    private sealed class EmptyLuaStatementSyntax : LuaStatementSyntax {
      internal override void Render(LuaRenderer renderer) {
      }
    }

    public readonly static LuaStatementSyntax Empty = new EmptyLuaStatementSyntax();
    public readonly static LuaStatementSyntax Colon = new LuaIdentifierNameSyntax(Semicolon.kSemicolon).ToStatement();
  }

  public sealed class LuaExpressionStatementSyntax : LuaStatementSyntax {
    public LuaExpressionSyntax Expression { get; }

    public LuaExpressionStatementSyntax(LuaExpressionSyntax expression) {
      Expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaStatementListSyntax : LuaStatementSyntax {
    public readonly LuaSyntaxList<LuaStatementSyntax> Statements = new LuaSyntaxList<LuaStatementSyntax>();

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaReturnStatementSyntax : LuaStatementSyntax {
    public LuaExpressionSyntax Expression { get; }
    public string ReturnKeyword => Tokens.Return;

    public LuaReturnStatementSyntax(LuaExpressionSyntax expression = null) {
      Expression = expression;
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaMultipleReturnStatementSyntax : LuaStatementSyntax {
    public LuaSyntaxList<LuaExpressionSyntax> Expressions { get; } = new LuaSyntaxList<LuaExpressionSyntax>();
    public string ReturnKeyword => Tokens.Return;

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaBreakStatementSyntax : LuaStatementSyntax {
    public string BreakKeyword => Tokens.Break;

    private LuaBreakStatementSyntax() { }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }

    public static readonly LuaBreakStatementSyntax Statement = new LuaBreakStatementSyntax();
  }

  public sealed class LuaContinueAdapterStatementSyntax : LuaStatementSyntax {
    public LuaExpressionStatementSyntax Assignment { get; }
    public LuaBreakStatementSyntax Break => LuaBreakStatementSyntax.Statement;

    private LuaContinueAdapterStatementSyntax() {
      Assignment = new LuaExpressionStatementSyntax(new LuaAssignmentExpressionSyntax(LuaIdentifierNameSyntax.Continue, LuaIdentifierNameSyntax.True));
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }

    public static readonly LuaContinueAdapterStatementSyntax Statement = new LuaContinueAdapterStatementSyntax();
  }

  public sealed class LuaBlankLinesStatement : LuaStatementSyntax {
    public int Count { get; }

    public LuaBlankLinesStatement(int count) {
      Count = count;
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }

    public static readonly LuaBlankLinesStatement One = new LuaBlankLinesStatement(1);
  }

  public sealed class LuaShortCommentStatement : LuaStatementSyntax {
    public string SingleCommentToken => Tokens.ShortComment;
    public string Comment { get; }

    public LuaShortCommentStatement(string comment) {
      Comment = comment;
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public class LuaShortCommentExpressionStatement : LuaStatementSyntax {
    public string SingleCommentToken => Tokens.ShortComment;
    public LuaExpressionSyntax Expression { get; }

    public LuaShortCommentExpressionStatement(LuaExpressionSyntax expression) {
      Expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaLongCommentStatement : LuaShortCommentExpressionStatement {
    public LuaLongCommentStatement(string comment) : base(new LuaVerbatimStringLiteralExpressionSyntax(comment, false)) {
    }
  }

  public sealed class LuaGotoStatement : LuaStatementSyntax {
    public LuaIdentifierNameSyntax Identifier { get; }
    public string GotoKeyword => Tokens.Goto;

    public LuaGotoStatement(LuaIdentifierNameSyntax identifier) {
      Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaGotoCaseAdapterStatement : LuaStatementSyntax {
    public LuaStatementSyntax Assignment { get; }
    public LuaGotoStatement GotoStatement { get; }

    public LuaGotoCaseAdapterStatement(LuaIdentifierNameSyntax identifier) {
      if (identifier == null) {
        throw new ArgumentNullException(nameof(identifier));
      }

      LuaAssignmentExpressionSyntax assignment = new LuaAssignmentExpressionSyntax(identifier, LuaIdentifierNameSyntax.True);
      Assignment = new LuaExpressionStatementSyntax(assignment);
      GotoStatement = new LuaGotoStatement(identifier);
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaLabeledStatement : LuaStatementSyntax {
    public string PrefixToken => Tokens.Label;
    public string SuffixToken => Tokens.Label;
    public LuaIdentifierNameSyntax Identifier { get; }
    public LuaStatementSyntax Statement { get; }

    public LuaLabeledStatement(LuaIdentifierNameSyntax identifier, LuaStatementSyntax statement = null) {
      Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
      Statement = statement;
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaDocumentStatement : LuaStatementSyntax {
    private const string kAttributePrefix = "@CSharpLua.";
    public const string kNoField = kAttributePrefix + nameof(AttributeFlags.NoField);

    [Flags]
    public enum AttributeFlags {
      None = 0,
      Ignore = 1 << 0,
      NoField = 1 << 1,
    }

    public readonly List<LuaStatementSyntax> Statements = new List<LuaStatementSyntax>();
    public bool IsEmpty => Statements.Count == 0;
    private AttributeFlags attr_;
    public bool HasIgnoreAttribute => attr_.HasFlag(AttributeFlags.Ignore);

    public LuaDocumentStatement() {
    }

    public LuaDocumentStatement(string triviaText) {
      var items = triviaText.Replace("///", string.Empty)
        .Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
        .Select(i => i.Trim()).ToList();

      int curIndex = 0;
      while (curIndex < items.Count) {
        int beginIndex = items.FindIndex(curIndex, i => i == Tokens.OpenSummary);
        if (beginIndex != -1) {
          AddLineText(items, curIndex, beginIndex);
          int endIndex = items.FindIndex(beginIndex + 1, it => it == Tokens.CloseSummary);
          Contract.Assert(endIndex != -1);
          LuaSummaryDocumentStatement summary = new LuaSummaryDocumentStatement();
          bool hasAttr = false;
          for (int i = beginIndex + 1; i < endIndex; ++i) {
            string text = items[i];
            if (IsAttribute(text, out AttributeFlags arrt)) {
              attr_ |= arrt;
              hasAttr = true;
            } else {
              summary.Texts.Add(text);
            }
          }
          if (summary.Texts.Count > 0 || !hasAttr) {
            Statements.Add(summary);
          }
          curIndex = endIndex + 1;
        } else {
          AddLineText(items, curIndex, items.Count);
          curIndex = items.Count;
        }
      }
    }

    private void AddLineText(List<string> items, int beginIndex, int endIndex) {
      for (int i = beginIndex + 1; i < endIndex; ++i) {
        string text = items[i];
        Statements.Add(new LuaLineDocumentStatement(text));
      }
    }

    private static bool IsAttribute(string text, out AttributeFlags attr) {
      attr = AttributeFlags.None;
      int index = text.IndexOf(kAttributePrefix);
      if (index != -1) {
        string s = text.Substring(index + kAttributePrefix.Length);
        if(Enum.TryParse(s, out attr)) {
          return true;
        } else {
          throw new CompilationErrorException($"{s} is not define attribute");
        }
      }
      return false;
    }

    public void Add(LuaDocumentStatement document) {
      Statements.AddRange(document.Statements);
      attr_ |= document.attr_;
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaSummaryDocumentStatement : LuaStatementSyntax {
    public string OpenSummary = Tokens.OpenSummary;
    public string CloseSummary = Tokens.CloseSummary;
    public readonly List<string> Texts = new List<string>();

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

  public sealed class LuaLineDocumentStatement : LuaStatementSyntax {
    public string Text { get; }

    public LuaLineDocumentStatement(string text) {
      Text = text;
    }

    internal override void Render(LuaRenderer renderer) {
      renderer.Render(this);
    }
  }

}
