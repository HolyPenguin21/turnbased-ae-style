"""Export the authored help text for editorial review without requiring Unity."""
from pathlib import Path
import argparse
from verify import ROOT, unity_objects

OUTPUT = ROOT / 'Docs/GameHelp_English_Content.md'


def render():
    catalog = unity_objects('Assets/Config/GameHelpCatalog.asset')[11400000][1]
    sections = catalog['sections']
    topic_count = sum(len(section['topics']) for section in sections)
    lines = [
        '# Game Help - English Content', '',
        '<!-- Generated from Assets/Config/GameHelpCatalog.asset; edit the asset, then run the exporter. -->', '',
        f'{topic_count} articles across {len(sections)} sections. English fallback text used by the in-game Help window.', '',
        'The rules describe the current implementation. Costs that vary by card are read from the displayed card or action.', '',
    ]
    for section in sections:
        lines += [f"## {section['title']['fallback']}", '']
        for topic in section['topics']:
            lines += [f"### {topic['title']['fallback']}", '', f"Topic ID: `{topic['id']}`", '']
            for block in topic.get('blocks') or []:
                text = (block.get('text') or {}).get('fallback') or ''
                if text:
                    lines += [('#### ' if block.get('kind') == 2 else '') + text, '']
    return '\n'.join(lines).rstrip() + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Check that the committed review document matches the asset.')
    args = parser.parse_args()
    content = render()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text() != content:
            raise SystemExit('English review document is out of date. Run the exporter without --check.')
        print('English review document matches the catalog.')
    else:
        OUTPUT.write_text(content)
        print(OUTPUT.relative_to(ROOT))
