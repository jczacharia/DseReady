// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using System.Text;

namespace Dse.Sources.Confluence;

/// <summary>
///     Streaming, allocation-light text extractor for Confluence storage-format HTML. Builds no DOM.
///     The previous AngleSharp-based implementation allocated tens of thousands of node objects per
///     page, promoted them to Gen2/LOH under sustained crawl load, and produced multi-GB heaps even
///     though nothing was actually retained (server GC simply could not keep up).
///     This implementation walks the input once with a small state stack and emits text into a
///     single StringBuilder. Behavioral parity with the prior cleaner is preserved by the test
///     suite in <c>ConfluenceHtmlCleanerTests</c>; the self-closing-tag bug class (the <c>time</c>
///     / <c>span</c> regressions) is naturally fixed because the tokenizer honors <c>/&gt;</c> for
///     any tag instead of relying on HTML5 void-element rules.
/// </summary>
public static class ConfluenceHtmlCleaner
{
    // Elements whose entire subtree contributes no text to the index. ri:user/ri:space/ri:url
    // belong here because they carry only opaque identifiers; ri:page and ri:attachment are
    // handled separately because their attributes are used as the ac:link fallback anchor.
    private static readonly HashSet<string> s_dropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac:default-parameter-value",
        "ac:emoticon",
        "ac:parameter",
        "ac:placeholder",
        "ac:task",
        "ac:task-id",
        "ac:task-list",
        "ac:task-status",
        "noscript",
        "ri:space",
        "ri:url",
        "ri:user",
        "script",
        "style",
        "template",
    };

    private static readonly HashSet<string> s_voidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        Scanner scanner = new(html);
        scanner.Run();
        return CollapseWhitespace(scanner.Output);
    }

    private static string CollapseWhitespace(StringBuilder input)
    {
        StringBuilder sb = new(input.Length);
        bool lastWasSpace = true;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private sealed class Frame
    {
        public bool Drop;

        // ac:link state.
        public bool IsLink;
        public StringBuilder? LinkBodySink;
        public string? LinkFallback;

        // ac:structured-macro state.
        public string? MacroName;
        public StringBuilder? PlainSink;
        public string Tag = string.Empty;

        // Where text emitted inside this element is appended. For most elements this is the
        // parent's sink (text bubbles up); structured-macro and ac:link own private sinks so the
        // element's contribution can be substituted (macro body / link anchor) on close.
        public StringBuilder TextSink = null!;
    }

    private sealed class Scanner
    {
        private readonly string _input;
        private readonly Stack<Frame> _stack = new();
        private int _dropDepth;
        private int _pos;

        public Scanner(string input)
        {
            _input = input;
            _stack.Push(new Frame { Tag = "#root", TextSink = Output });
        }

        public StringBuilder Output { get; } = new();

        public void Run()
        {
            while (_pos < _input.Length)
            {
                char c = _input[_pos];
                if (c == '<')
                {
                    if (StartsWith("<!--"))
                    {
                        SkipUntil("-->");
                        continue;
                    }

                    if (StartsWith("<![CDATA["))
                    {
                        ReadCData();
                        continue;
                    }

                    if (StartsWith("<!") || StartsWith("<?"))
                    {
                        SkipTo('>');
                        continue;
                    }

                    if (_pos + 1 < _input.Length &&
                        (IsNameStart(_input[_pos + 1]) || _input[_pos + 1] == '/'))
                    {
                        ReadTag();
                        continue;
                    }

                    // Lone '<' — treat as literal text.
                    AppendText("<");
                    _pos++;
                }
                else
                {
                    ReadText();
                }
            }

            // Best-effort close of any still-open frames (malformed input).
            while (_stack.Count > 1)
            {
                Frame f = _stack.Pop();
                if (f.Drop)
                {
                    _dropDepth--;
                }

                ApplyClose(f);
            }
        }

        private bool StartsWith(string prefix) =>
            _pos + prefix.Length <= _input.Length &&
            _input.AsSpan(_pos, prefix.Length).SequenceEqual(prefix.AsSpan());

        private void SkipUntil(string terminator)
        {
            int idx = _input.IndexOf(terminator, _pos, StringComparison.Ordinal);
            _pos = idx < 0 ? _input.Length : idx + terminator.Length;
        }

        private void SkipTo(char terminator)
        {
            int idx = _input.IndexOf(terminator, _pos);
            _pos = idx < 0 ? _input.Length : idx + 1;
        }

        private void ReadCData()
        {
            _pos += "<![CDATA[".Length;
            int end = _input.IndexOf("]]>", _pos, StringComparison.Ordinal);
            int contentEnd = end < 0 ? _input.Length : end;

            if (_dropDepth == 0 && contentEnd > _pos)
            {
                // CDATA content is literal — no entity decoding.
                _stack.Peek().TextSink.Append(_input, _pos, contentEnd - _pos);
            }

            _pos = end < 0 ? _input.Length : end + 3;
        }

        private void ReadText()
        {
            int start = _pos;
            while (_pos < _input.Length && _input[_pos] != '<')
            {
                _pos++;
            }

            if (_pos > start && _dropDepth == 0)
            {
                string raw = _input.Substring(start, _pos - start);
                AppendText(WebUtility.HtmlDecode(raw));
            }
        }

        private void AppendText(string text)
        {
            if (_dropDepth > 0)
            {
                return;
            }

            _stack.Peek().TextSink.Append(text);
        }

        private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c == ':';

        private static bool IsNameChar(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '-' || c == '.';

        private void SkipWs()
        {
            while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
            {
                _pos++;
            }
        }

        private void ReadTag()
        {
            _pos++; // skip '<'
            bool closing = false;
            if (_pos < _input.Length && _input[_pos] == '/')
            {
                closing = true;
                _pos++;
            }

            int nameStart = _pos;
            while (_pos < _input.Length && IsNameChar(_input[_pos]))
            {
                _pos++;
            }

            string tag = _input.Substring(nameStart, _pos - nameStart);

            Dictionary<string, string>? attrs = null;
            bool selfClose = false;

            while (_pos < _input.Length)
            {
                SkipWs();
                if (_pos >= _input.Length)
                {
                    break;
                }

                char c = _input[_pos];
                if (c == '>')
                {
                    _pos++;
                    break;
                }

                if (c == '/' && _pos + 1 < _input.Length && _input[_pos + 1] == '>')
                {
                    selfClose = true;
                    _pos += 2;
                    break;
                }

                if (!IsNameStart(c))
                {
                    _pos++; // skip stray characters defensively
                    continue;
                }

                int anStart = _pos;
                while (_pos < _input.Length && IsNameChar(_input[_pos]))
                {
                    _pos++;
                }

                string attrName = _input.Substring(anStart, _pos - anStart);
                string attrValue = string.Empty;

                SkipWs();
                if (_pos < _input.Length && _input[_pos] == '=')
                {
                    _pos++;
                    SkipWs();
                    if (_pos < _input.Length && (_input[_pos] == '"' || _input[_pos] == '\''))
                    {
                        char quote = _input[_pos++];
                        int vs = _pos;
                        while (_pos < _input.Length && _input[_pos] != quote)
                        {
                            _pos++;
                        }

                        attrValue = _input.Substring(vs, _pos - vs);
                        if (_pos < _input.Length)
                        {
                            _pos++;
                        }
                    }
                    else
                    {
                        int vs = _pos;
                        while (_pos < _input.Length &&
                               !char.IsWhiteSpace(_input[_pos]) &&
                               _input[_pos] != '>' &&
                               _input[_pos] != '/')
                        {
                            _pos++;
                        }

                        attrValue = _input.Substring(vs, _pos - vs);
                    }
                }

                if (attrName.Length > 0)
                {
                    attrs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    attrs[attrName] = attrValue;
                }
            }

            if (tag.Length == 0)
            {
                return;
            }

            if (closing)
            {
                HandleClose(tag);
            }
            else
            {
                HandleOpen(tag, attrs, selfClose);
            }
        }

        private Frame? FindAncestor(string tag) =>
            _stack.FirstOrDefault(f => string.Equals(f.Tag, tag, StringComparison.OrdinalIgnoreCase));

        private void HandleOpen(string tag, Dictionary<string, string>? attrs, bool selfClose)
        {
            // Void HTML5 elements never push a frame; emit a word boundary and move on.
            if (s_voidTags.Contains(tag))
            {
                if (_dropDepth == 0)
                {
                    StringBuilder sink = _stack.Peek().TextSink;
                    if (sink.Length > 0 && sink[^1] != ' ')
                    {
                        sink.Append(' ');
                    }
                }

                return;
            }

            // ri:page / ri:attachment are leaf reference elements whose attribute supplies the
            // fallback anchor for an enclosing ac:link. They contribute no text themselves.
            if (string.Equals(tag, "ri:page", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "ri:attachment", StringComparison.OrdinalIgnoreCase))
            {
                if (_dropDepth == 0 && attrs is not null)
                {
                    Frame? link = FindAncestor("ac:link");
                    if (link is not null && string.IsNullOrEmpty(link.LinkFallback))
                    {
                        string key = string.Equals(tag, "ri:page", StringComparison.OrdinalIgnoreCase)
                            ? "ri:content-title"
                            : "ri:filename";
                        if (attrs.TryGetValue(key, out string? val) &&
                            !string.IsNullOrWhiteSpace(val))
                        {
                            link.LinkFallback = WebUtility.HtmlDecode(val).Trim();
                        }
                    }
                }

                if (!selfClose)
                {
                    PushFrame(new Frame
                    {
                        Tag = tag,
                        Drop = true,
                        TextSink = _stack.Peek().TextSink,
                    });
                }

                return;
            }

            Frame parent = _stack.Peek();
            Frame frame = new()
                { Tag = tag };

            if (s_dropTags.Contains(tag))
            {
                frame.Drop = true;
                frame.TextSink = parent.TextSink;
            }
            else if (string.Equals(tag, "ac:structured-macro", StringComparison.OrdinalIgnoreCase))
            {
                frame.MacroName = attrs is not null && attrs.TryGetValue("ac:name", out string? name)
                    ? name
                    : string.Empty;
                frame.TextSink = new StringBuilder();
                frame.PlainSink = new StringBuilder();
            }
            else if (string.Equals(tag, "ac:link", StringComparison.OrdinalIgnoreCase))
            {
                frame.IsLink = true;
                frame.LinkBodySink = new StringBuilder();
                // Direct text inside ac:link (outside a body sub-element) is uncommon and not part
                // of the indexed anchor — route to a scrap buffer so it cannot leak into the parent.
                frame.TextSink = new StringBuilder();
            }
            else if (string.Equals(tag, "ac:plain-text-body", StringComparison.OrdinalIgnoreCase))
            {
                Frame? macro = FindAncestor("ac:structured-macro");
                frame.TextSink = macro?.PlainSink ?? parent.TextSink;
            }
            else if (string.Equals(tag, "ac:link-body", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(tag, "ac:plain-text-link-body", StringComparison.OrdinalIgnoreCase))
            {
                Frame? link = FindAncestor("ac:link");
                frame.TextSink = link?.LinkBodySink ?? parent.TextSink;
            }
            else
            {
                frame.TextSink = parent.TextSink;
            }

            if (selfClose)
            {
                // Push briefly so ApplyClose sees the correct parent, then pop.
                ApplyClose(frame);
            }
            else
            {
                PushFrame(frame);
            }
        }

        private void PushFrame(Frame f)
        {
            if (f.Drop)
            {
                _dropDepth++;
            }

            _stack.Push(f);
        }

        private void HandleClose(string tag)
        {
            bool matchInStack = _stack.Any(f => string.Equals(f.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (!matchInStack)
            {
                return; // stray close tag — ignore
            }

            while (_stack.Count > 1)
            {
                Frame f = _stack.Pop();
                if (f.Drop)
                {
                    _dropDepth--;
                }

                ApplyClose(f);
                if (string.Equals(f.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        private void ApplyClose(Frame f)
        {
            // If the closing frame itself was dropping, or any remaining ancestor is dropping,
            // suppress any substitution emission.
            if (f.Drop || _dropDepth > 0)
            {
                return;
            }

            StringBuilder parentSink = _stack.Count > 0 ? _stack.Peek().TextSink : Output;

            if (string.Equals(f.Tag, "ac:structured-macro", StringComparison.OrdinalIgnoreCase))
            {
                string plain = f.PlainSink?.ToString() ?? string.Empty;
                string rich = f.TextSink.ToString();
                string body = !string.IsNullOrWhiteSpace(plain) ? plain : rich;

                // The `html` macro's plain-text-body contains real HTML markup (we read it from
                // CDATA literally). Re-tokenize to extract its text rather than letting the tag
                // text leak verbatim into the index. Code/noformat/etc. macros are intentionally
                // left verbatim — their bodies are source, not markup.
                if (!string.IsNullOrWhiteSpace(body) &&
                    string.Equals(f.MacroName, "html", StringComparison.OrdinalIgnoreCase))
                {
                    Scanner inner = new(body);
                    inner.Run();
                    body = inner.Output.ToString();
                }

                if (!string.IsNullOrWhiteSpace(body))
                {
                    parentSink.Append(' ').Append(body).Append(' ');
                }

                return;
            }

            if (f.IsLink)
            {
                string anchor = (f.LinkBodySink?.ToString() ?? string.Empty).Trim();
                if (anchor.Length == 0 && !string.IsNullOrEmpty(f.LinkFallback))
                {
                    anchor = f.LinkFallback;
                }

                if (anchor.Length > 0)
                {
                    parentSink.Append(' ').Append(anchor).Append(' ');
                }

                return;
            }

            // ac:plain-text-body / ac:link-body / ac:plain-text-link-body content was routed to
            // a special sink during parsing; the close is a no-op (no boundary needed because the
            // enclosing macro/link emits its own surrounding spaces on close).
            if (string.Equals(f.Tag, "ac:plain-text-body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Tag, "ac:link-body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Tag, "ac:plain-text-link-body", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Regular element close — add a word boundary so adjacent block content tokenizes
            // independently (e.g., adjacent table cells, list items, paragraphs).
            if (parentSink.Length > 0 && parentSink[^1] != ' ')
            {
                parentSink.Append(' ');
            }
        }
    }
}
