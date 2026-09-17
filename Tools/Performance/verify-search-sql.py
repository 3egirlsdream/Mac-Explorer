#!/usr/bin/env python3
"""SQLite regression harness. Uses the ACTUAL C# schema/UPSERT constants.

This is not a C# compiler or a substitute for dotnet test/native macOS validation.
The query builder below mirrors SearchCatalog for cross-checking FTS candidates
against literal matching. Only Python's standard library is required.
"""
from pathlib import Path
import re
import sqlite3
import unicodedata
import unittest
import random
import string

_QUERY_CONTEXTS = {}

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = (ROOT / "Indexing/SearchSchema.cs").read_text()
CATALOG = (ROOT / "Indexing/SearchCatalog.cs").read_text()


def constant(source, name):
    match = re.search(r'const string ' + re.escape(name) + r'\s*=\s*"""\n(.*?)\n\s*""";', source, re.S)
    if not match:
        raise AssertionError(f"Missing C# SQL constant: {name}")
    return match.group(1)


TABLES = constant(SCHEMA, "TablesSql")
FTS = constant(SCHEMA, "FtsSql")
UPSERT = constant(CATALOG, "UpsertSql")


def fold(text):
    return unicodedata.normalize("NFC", text).upper()


def within(path, root):
    return path == root or path.startswith(root.rstrip("/") + "/")


def visible(path, name, directory, root, show_hidden=False):
    if name.endswith(".fkfinder-tmp"):
        return False
    if show_hidden:
        return True
    return not any(part.startswith(".") for part in path[len(root.rstrip('/')) + 1:].split('/'))


def item(path, scan="one", directory=False, initials="", link=False):
    name = path.rsplit('/', 1)[-1]
    parent = path.rsplit('/', 1)[0] or '/'
    ext = ('.' + name.rsplit('.', 1)[1]) if '.' in name and not name.startswith('.') else ''
    return dict(path=path, name=name, parent=parent, ext=ext, extKey=fold(ext.lstrip('.')),
                nameKey=fold(name), initials=initials, parentKey=fold(parent), size=12,
                dir=int(directory), hidden=int(name.startswith('.')), created=621355968000000000,
                modified=621355968000000000, link=int(link), scan=scan)


def scope(column="e.path"):
    return f"({column}=@root COLLATE BINARY OR ({column}>=@prefix COLLATE BINARY AND {column}<@upper COLLATE BINARY))"


def scope_params(root):
    prefix = root.rstrip('/') + '/'
    return dict(root=root, prefix=prefix, upper=prefix[:-1] + '0')


def query(db, root='/scope', terms=(), parents=(), extensions=(), limit=50, pinyin=True,
          use_fts=True, show_hidden=False, ai=False):
    params = scope_params(root)
    params.update(exact=' '.join(map(fold, terms)), first=fold(terms[0]) if terms else '', limit=limit)
    # Register once per connection, just as one C# SearchAsync operation does.
    # Python caches statements; replacing a UDF between queries can fail while
    # FTS retains an internal prepared statement.
    if id(db) not in _QUERY_CONTEXTS:
        context = {}
        _QUERY_CONTEXTS[id(db)] = context
        db.create_function('search_visible', 3, lambda p, n, d: visible(p, n, d, context['root'], context['hidden']))
        db.create_function('search_fold', 1, fold)
    _QUERY_CONTEXTS[id(db)].update(root=root, hidden=show_hidden)
    predicates = [scope(), 'search_visible(e.path,e.name,e.is_directory)']
    candidates = []
    quote = lambda value: '"' + value.replace('"', '""') + '"'
    for i, term in enumerate(map(fold, terms)):
        param = f'name{i}'
        params[param] = term
        name_match = f'(instr(e.name_key,@{param})>0 OR instr(e.initials_key,@{param})>0)' if pinyin else f'instr(e.name_key,@{param})>0'
        predicates.append(f'({name_match} OR EXISTS(SELECT 1 FROM ai_tags a WHERE a.file_path=e.path AND instr(search_fold(a.tag_value),@{param})>0))' if ai else name_match)
        if len(term) >= 3:
            candidates.append(f'(name_key : {quote(term)} OR initials_key : {quote(term)})' if pinyin else f'name_key : {quote(term)}')
    for i, term in enumerate(map(fold, parents)):
        params[f'parent{i}'] = term
        predicates.append(f'instr(e.parent_key,@parent{i})>0')
        if len(term) >= 3:
            candidates.append(f'parent_key : {quote(term)}')
    if extensions:
        predicates.append('e.extension_key IN (' + ','.join(f'@ext{i}' for i in range(len(extensions))) + ')')
        params.update({f'ext{i}': fold(ext.lstrip('.')) for i, ext in enumerate(extensions)})
    if candidates and use_fts and not ai:
        predicates.append('e.id IN (SELECT rowid FROM search_names WHERE search_names MATCH @fts)')
        params['fts'] = ' AND '.join(candidates)
    source = f'(SELECT DISTINCT file_path FROM ai_tags WHERE {scope("file_path")}) tagged JOIN search_entries e ON e.path=tagged.file_path' if ai else 'search_entries e'
    sql = f'''SELECT e.path FROM {source} WHERE {' AND '.join(predicates)}
        ORDER BY CASE WHEN e.name_key=@exact THEN 0 WHEN @first<>'' AND instr(e.name_key,@first)=1 THEN 1 ELSE 2 END,
                 e.name_key COLLATE BINARY,e.path COLLATE BINARY LIMIT @limit'''
    cursor = db.execute(sql, params)
    try:
        return [row[0] for row in cursor]
    finally:
        cursor.close()


class SearchSqlTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(':memory:')
        self.db.executescript(TABLES)
        self.db.executescript(FTS)

    def tearDown(self):
        _QUERY_CONTEXTS.pop(id(self.db), None)
        self.db.close()

    def insert(self, *items):
        self.db.executemany(UPSERT, items)

    def integrity(self):
        self.db.execute("INSERT INTO search_names(search_names,rank) VALUES('integrity-check',1)")

    def finish_directory(self, directory, scan):
        # Mirrors CompleteDirectoryAsync; deleting only AFTER successful enumeration.
        removed = self.db.execute('SELECT path FROM search_entries WHERE parent_path=? AND scan_id<>? AND is_directory=1', (directory, scan)).fetchall()
        for (path,) in removed:
            self.db.execute('DELETE FROM search_entries WHERE ' + scope('path'), scope_params(path))
        self.db.execute('DELETE FROM search_entries WHERE parent_path=? AND scan_id<>?', (directory, scan))

    def test_name_and_ai_terms_can_be_combined(self):
        self.db.execute('CREATE TABLE ai_tags(file_path TEXT,tag_value TEXT)')
        self.insert(item('/scope/holiday.png'), item('/scope/other.png'))
        self.db.executemany('INSERT INTO ai_tags VALUES(?,?)', [('/scope/holiday.png', 'sunset'), ('/scope/other.png', 'sunset')])
        self.assertEqual(query(self.db, terms=['holiday', 'sunset'], ai=True), ['/scope/holiday.png'])
        self.assertEqual(query(self.db, terms=['holiday', 'sunset'], ai=False), [])

    def test_unchanged_explicit_symlink_root_keeps_its_indexed_children(self):
        self.insert(item('/scope/link', directory=True, link=True), item('/scope/link/report.pdf'))
        self.insert(item('/scope/link', scan='two', directory=True, link=True))
        self.assertEqual(query(self.db, terms=['report']), ['/scope/link/report.pdf'])
        self.integrity()

    def test_scope_and_visibility_before_limit(self):
        self.insert(*(item(f'/outside/a-report-{i}.pdf') for i in range(1200)))
        self.insert(*(item(f'/scope/.hidden/a-report-{i}.pdf') for i in range(600)))
        self.insert(item('/scope/zzz-report.pdf'))
        self.assertEqual(query(self.db, terms=['report'], limit=1), ['/scope/zzz-report.pdf'])

    def test_boundary_and_case_distinct_paths(self):
        self.insert(*(item(path) for path in ['/scope/report.pdf', '/scope/Report.pdf', '/scope-other/report.pdf', '/Scope/report.pdf']))
        self.assertEqual(len(query(self.db, terms=['report'])), 2)
        self.assertEqual(len(query(self.db, root='/', terms=['report'])), 4)

    def test_literal_punctuation_and_short_chinese(self):
        for name in ['100%_final.pdf', '100XXfinal.pdf', 'a_b.pdf', 'axb.pdf', '项目合同.pdf', 'a"b.pdf']:
            self.insert(item('/scope/' + name))
        for term, expected in [('100%', '100%_final.pdf'), ('a_b', 'a_b.pdf'), ('合同', '项目合同.pdf'),
                               ('目合同', '项目合同.pdf'), ('a"b', 'a"b.pdf')]:
            with self.subTest(term=term):
                a = query(self.db, terms=[term])
                b = query(self.db, terms=[term], use_fts=False)
                self.assertEqual(a, ['/scope/' + expected])
                self.assertEqual(a, b)

    def test_pinyin_extensions_and_parent_phrase(self):
        self.insert(item('/scope/My Projects/合同2026.PDF', initials='HT2026.PDF'),
                    item('/scope/Other/合同2026.PDF', initials='HT2026.PDF'),
                    item('/scope/My Projects/合同2026.png', initials='HT2026.PNG'))
        kwargs = dict(terms=['ht', '2026'], parents=['my projects'], extensions=['pdf'])
        self.assertEqual(query(self.db, **kwargs), ['/scope/My Projects/合同2026.PDF'])
        self.assertEqual(query(self.db, pinyin=False, **kwargs), [])

    def test_canonical_names_do_not_collapse_paths(self):
        self.insert(item('/scope/café.pdf'), item('/scope/cafe\u0301.pdf'))
        self.assertEqual(len(query(self.db, terms=['café'])), 2)
        self.integrity()

    def test_rescan_stable_rowid(self):
        self.insert(item('/scope/report.pdf'))
        before = self.db.execute('SELECT id FROM search_entries').fetchone()
        self.insert(item('/scope/report.pdf', scan='two'))
        self.finish_directory('/scope', 'two')
        self.assertEqual(self.db.execute('SELECT id FROM search_entries').fetchone(), before)
        self.assertEqual(query(self.db, terms=['report']), ['/scope/report.pdf'])
        self.integrity()

    def test_deleted_subtree_removed_atomically(self):
        self.insert(item('/scope/old', directory=True), item('/scope/old/secret.pdf'))
        self.finish_directory('/scope', 'new')
        self.assertEqual(query(self.db, terms=['secret']), [])
        self.integrity()

    def test_directory_replaced_by_file(self):
        self.insert(item('/scope/old', directory=True), item('/scope/old/secret.pdf'))
        self.insert(item('/scope/old', scan='two', directory=False))
        self.assertEqual(query(self.db, terms=['secret']), [])
        self.integrity()

    def test_directory_replaced_by_symlink(self):
        self.insert(item('/scope/old', directory=True), item('/scope/old/secret.pdf'))
        self.insert(item('/scope/old', scan='two', directory=True, link=True))
        self.assertEqual(query(self.db, terms=['secret']), [])
        self.integrity()

    def test_failed_or_cancelled_scan_retains_existing_rows(self):
        self.insert(item('/scope/old-report.pdf'))
        self.insert(item('/scope/new-report.pdf', scan='two'))
        self.assertEqual(len(query(self.db, terms=['report'])), 2)
        self.integrity()

    def test_fts_update_removes_old_tokens(self):
        old = item('/scope/stable.pdf')
        self.insert(old)
        self.insert(dict(old, name='changed.pdf', nameKey='CHANGED.PDF', scan='two'))
        self.assertEqual(query(self.db, terms=['stable']), [])
        self.assertEqual(query(self.db, terms=['changed']), ['/scope/stable.pdf'])
        self.integrity()

    def test_reinitialization_preserves_user_data(self):
        self.db.executescript("CREATE TABLE collections(name TEXT); INSERT INTO collections VALUES('do not delete')")
        self.insert(item('/scope/report.pdf'))
        self.db.executescript(TABLES)
        self.db.executescript(FTS)
        self.assertEqual(self.db.execute('SELECT name FROM collections').fetchone(), ('do not delete',))
        self.assertEqual(len(query(self.db, terms=['report'])), 1)
        self.integrity()

    def test_fts_added_after_rows_are_present(self):
        other = sqlite3.connect(':memory:')
        try:
            other.executescript(TABLES)
            other.execute(UPSERT, item('/scope/report.pdf'))
            other.executescript(FTS)
            other.execute("INSERT INTO search_names(search_names) VALUES('rebuild')")
            self.assertEqual(query(other, terms=['report']), ['/scope/report.pdf'])
        finally:
            _QUERY_CONTEXTS.pop(id(other), None)
            other.close()

    def test_ai_paths_are_scoped_before_limit(self):
        self.db.executescript('CREATE TABLE ai_tags(file_path TEXT,tag_value TEXT); CREATE INDEX tag_path ON ai_tags(file_path)')
        self.insert(item('/outside/photo.png'), item('/scope/photo.png'))
        self.db.executemany('INSERT INTO ai_tags VALUES(?,?)', [('/outside/photo.png', 'sunset'), ('/scope/photo.png', 'sunset')])
        self.assertEqual(query(self.db, terms=['sunset'], extensions=['png'], ai=True, limit=1), ['/scope/photo.png'])

    def test_checkpoint_unsigned_roundtrip(self):
        value = str(2**64 - 2)
        self.db.execute('INSERT INTO search_roots(path,checkpoint) VALUES(?,?)', ('/scope', value))
        self.assertEqual(int(self.db.execute('SELECT checkpoint FROM search_roots').fetchone()[0]), 2**64 - 2)

    def test_trigram_candidates_never_change_exact_membership_fuzz(self):
        rng = random.Random(17)
        alphabet = string.ascii_letters + '合同项目报告%_ -()[]"é😀'
        records = []
        for i in range(180):
            name = ''.join(rng.choice(alphabet) for _ in range(16)) + '.pdf'
            records.append(item('/scope/' + str(i) + '-' + name))
        self.insert(*records)
        for record in records:
            name = record['name']
            start = rng.randrange(len(name) - 5)
            for length in (1, 2, 3, 5):
                term = name[start:start + length]
                self.assertEqual(query(self.db, terms=[term]), query(self.db, terms=[term], use_fts=False), term)
        self.integrity()


if __name__ == '__main__':
    print(f'SQLite {sqlite3.sqlite_version}; C# constants read from {ROOT}', flush=True)
    unittest.main(verbosity=2)
