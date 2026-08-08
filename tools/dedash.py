"""Replace em-dashes with real punctuation, choosing per context.

The rule this enforces is not "swap U+2014 for something else" -- that keeps the
rhetorical tic and just makes it uglier. Each dash is resolved to the punctuation
the sentence actually wanted:

    :   a label followed by its detail        **DONE** - mode toggle, signals
    ,   a subordinate / relative continuation  X, which is why ...
    .   a following independent sentence       X. The whole point is ...
    ;   a following clause that elaborates     X; nothing checks a pairing

Run with --dry to print decisions without writing.
"""
import argparse
import os
import re
import sys
from collections import Counter

# Extensions that carry prose (comments, docs, help text, log messages).
EXTS = ('.md', '.cs', '.py', '.ps1', '.csproj')
SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules', '.vs', 'packages', 'TestResults'}

# A following clause opening with one of these is a continuation, not a new
# sentence, so it wants a comma. Coordinators, subordinators, prepositions and
# relative pronouns: none of them can stand alone after a semicolon.
COMMA_STARTS = {
    'and', 'but', 'or', 'nor', 'so', 'yet', 'plus', 'rather', 'especially',
    'including', 'like', 'such', 'with', 'without', 'though', 'although',
    'even', 'per', 'mostly', 'largely', 'chiefly', 'typically', 'usually',
    'often', 'sometimes', 'always', 'never', 'only', 'just', 'merely',
    'simply', 'roughly', 'about', 'up', 'down', 'by', 'for',
    'from', 'of', 'to', 'as', 'via', 'until', 'unless', 'while', 'when',
    'where', 'which', 'whose', 'who', 'whom', 'that', 'because', 'since',
    'if', 'after', 'before', 'both', 'each', 'either', 'neither', 'all',
    'not', 'hence', 'thereby', 'thus', 'whereas', 'whether', 'against',
    'between', 'across', 'along', 'around', 'beyond', 'despite', 'during',
    'into', 'onto', 'over', 'through', 'toward', 'towards', 'under', 'upon',
    'within', 'e.g.', 'i.e.', 'ie', 'eg', 'once', 'here', 'now', 'then',
}

# Leading markdown / comment furniture to strip before judging a line's shape.
COMMENT_LEAD = re.compile(r'^\s*(?://+|/\*+|\*(?!\*)|#(?=\s))\s*')
LIST_LEAD = re.compile(r'^\s*(?:[-*+>]\s+|\d+[.)]\s+)(?:\[[ xX]\]\s*)?')
HEADING_LEAD = re.compile(r'^\s*#{1,6}\s+')
LINK_ONLY = re.compile(r'^\[[^\]]+\]\([^)]+\)$')

DASH = re.compile('([ \\t]*\\n?[ \\t]*)\u2014([ \\t]*\\n?[ \\t]*)')


def line_before(text, idx):
    """The current line up to idx as (fragment, kind), furniture stripped.

    kind drives whether a short fragment counts as a label; a bare paragraph
    that happens to be short is still a sentence, a list item usually is not.
    """
    start = text.rfind('\n', 0, idx) + 1
    line = text[start:idx]
    kind = 'plain'
    if HEADING_LEAD.match(line):
        return HEADING_LEAD.sub('', line).strip(), 'heading'
    if '|' in line:                      # a table row: judge the current cell only
        line, kind = line.rsplit('|', 1)[-1], 'table'
    stripped = COMMENT_LEAD.sub('', line)
    if stripped != line:
        line, kind = stripped, 'comment'
    stripped = LIST_LEAD.sub('', line)
    if stripped != line:
        line, kind = stripped, 'list'
    return line.strip(), kind


def is_label(frag, kind):
    """True when the text before the dash names a thing, rather than saying something.

    A label wants a colon: the dash was standing in for 'is'.
    """
    if not frag:
        return False
    # Exactly one bold span, one code span, or one link, filling the fragment.
    if frag.startswith('**') and frag.endswith('**') and frag.count('**') == 2:
        return True
    if frag.startswith('`') and frag.endswith('`') and frag.count('`') == 2:
        return True
    if LINK_ONLY.match(frag):
        return True
    # A short fragment closing on bold or code reads as a label too
    # ("**Phase 4** -", "`Foo.Bar` -"), but a long one is a sentence.
    if len(frag) <= 55 and (frag.endswith('**') or frag.endswith('`')):
        return True
    # In a list item, table cell or comment, a short opener is a label for what
    # follows ("cmd_id 5 - query current step position").
    if kind in ('list', 'table', 'comment') and len(frag) <= 45:
        return True
    return False


def pair_positions(text, spans):
    """Indices of dashes acting as a matched pair around a parenthetical.

    "the flag below - and the branch further down - are unchanged" needs commas
    on both sides; a semicolon there strands the verb.
    """
    paired = set()
    for a, b in zip(spans, spans[1:]):
        gap = text[a[1]:b[0]]
        if len(gap) > 170 or '\n\n' in gap:
            continue
        if re.search(r'[.!?]\s', gap) or '—' in gap:
            continue
        # A new line opening with list or comment furniture is a sibling item,
        # not the far side of a parenthetical.
        if re.search(r'\n\s*(?://|/\*|\*|[-+|#]|\d+[.)])', gap):
            continue
        paired.add(a[0])
        paired.add(b[0])
    return paired


def first_word(frag):
    m = re.match(r"[\"'(\[]*([A-Za-z][A-Za-z.'-]*)", frag)
    return m.group(1) if m else ''


def decide(text, idx, after, paired):
    """Choose the punctuation that replaces the dash at idx."""
    frag, kind = line_before(text, idx)
    before_all = text[:idx].rstrip()

    # Already punctuated: the dash is pure decoration, so drop it.
    if before_all and before_all[-1] in '.,;:!?':
        return ''

    # Both halves of a parenthetical take commas.
    if idx in paired:
        return ','

    # Inside an unclosed bracket, a semicolon reads as a second statement.
    if frag.count('(') > frag.count(')'):
        return ','

    if kind == 'heading':
        return ':' if ':' not in frag else ','

    has_colon = ':' in frag

    # Checked before the label rule: a relative or subordinate opener continues
    # the sentence, so it can never be the detail half of a "label: detail".
    word = first_word(after)
    low = word.lower().rstrip('.')
    if low in COMMA_STARTS or word.lower() in COMMA_STARTS:
        return ','

    if is_label(frag, kind) and not has_colon:
        return ':'

    # A value, count or measurement following is the label's answer.
    if re.match(r'^[0-9~<>+-]', after):
        return ':' if not has_colon else ','

    # A capitalised opener that is not an acronym or an identifier starts a new
    # sentence. ALL-CAPS and CamelCase are names, so they do not.
    if word and word[0].isupper():
        if word.isupper() or re.match(r'^[A-Z][a-z]+[A-Z]', word):
            return ';'
        return '.'

    # Markup opener (`code`, **bold**, [link]) tells us nothing about case.
    if after[:1] in ('`', '*', '[', '"', '('):
        return ';'

    return ';'


def convert(text):
    stats = Counter()
    samples = []
    spans = [(m.start(), m.end()) for m in DASH.finditer(text)]
    paired = pair_positions(text, spans)

    def repl(m):
        whole = m.group(0)
        if '\n\n' in whole:              # never reach across a blank line
            return whole
        ws1, ws2 = m.group(1), m.group(2)
        idx = m.start()
        after = text[m.end():m.end() + 220].lstrip()
        punct = decide(text, idx, after, paired)
        stats[punct or '(dropped)'] += 1
        if len(samples) < 4000:
            before = text[max(0, idx - 70):idx].replace('\n', '\\n')
            samples.append((punct, before, after[:70].replace('\n', '\\n')))
        if '\n' in ws1:
            return punct + ws1.lstrip(' \t')
        if '\n' in ws2:
            return punct + ws2
        return punct + ' '

    return DASH.sub(repl, text), stats, samples


def walk(root):
    me = os.path.basename(__file__)
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for f in filenames:
            # Skip this file: it holds a literal dash in its own pattern, and a
            # self-rewrite silently turns the matcher into a matcher for commas.
            if f.endswith(EXTS) and f != me:
                yield os.path.join(dirpath, f)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('root', nargs='?', default='.')
    ap.add_argument('--dry', action='store_true')
    ap.add_argument('--show', type=int, default=0, help='print N sample decisions')
    ap.add_argument('--only', default=None, help='substring filter on path')
    args = ap.parse_args()

    total = Counter()
    all_samples = []
    changed = 0
    for path in walk(args.root):
        if args.only and args.only not in path.replace('\\', '/'):
            continue
        with open(path, encoding='utf-8', newline='') as fh:
            text = fh.read()
        if '; ' not in text:
            continue
        new, stats, samples = convert(text)
        if new == text:
            continue
        total.update(stats)
        all_samples.extend((path, s) for s in samples)
        changed += 1
        if not args.dry:
            with open(path, 'w', encoding='utf-8', newline='') as fh:
                fh.write(new)

    print(f'files: {changed}')
    for k, v in total.most_common():
        print(f'  {v:6d}  ->  {k!r}')
    if args.show:
        step = max(1, len(all_samples) // args.show)
        for path, (punct, before, after) in all_samples[::step][:args.show]:
            print(f'\n[{punct or "DROP"}] {os.path.basename(path)}')
            print(f'   ...{before}')
            print(f'   >>> {after}...')


if __name__ == '__main__':
    sys.exit(main())
