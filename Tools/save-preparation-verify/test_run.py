"""Behavioral checks for baseline selection; no SDK or Unity installation needed."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


RUNNER = Path(__file__).with_name('run.py')
SPEC = importlib.util.spec_from_file_location('save_preparation_verify', RUNNER)
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


class BaselineCliTests(unittest.TestCase):
    def assert_rejected_before_checks(self, arguments, diagnostic):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary) / 'checks must not start'
            root = Path(temporary) / 'repository'
            runner = root / 'Tools/save-preparation-verify/run.py'
            runner.parent.mkdir(parents=True)
            runner.write_bytes(RUNNER.read_bytes())
            subprocess.run(['git', 'init', '-q', str(root)], check=True)
            subprocess.run(['git', '-c', 'user.name=Verification Tests', '-c',
                            'user.email=verification@example.invalid',
                            'commit', '--allow-empty', '-qm', 'baseline'],
                           cwd=root, check=True)
            environment = dict(os.environ, SAVE_VERIFY_WORK=str(work))
            result = subprocess.run(
                [sys.executable, str(runner), '--binding', '--dotnet',
                 str(Path(temporary) / 'no SDK'), *arguments],
                capture_output=True, text=True, env=environment)
            self.assertNotEqual(0, result.returncode)
            self.assertIn(diagnostic, result.stderr)
            self.assertNotIn('Traceback', result.stderr)
            self.assertFalse(work.exists())

    def test_binding_requires_explicit_base_before_sdk_checks(self):
        self.assert_rejected_before_checks([], '--base is required with --binding')

    def test_invalid_base_is_rejected_before_sdk_checks(self):
        self.assert_rejected_before_checks(
            ['--base', 'no-such-save-verify-baseline'], 'not a valid commit')


class FixtureDependencyTests(unittest.TestCase):
    def test_selected_fixtures_require_nunit_instead_of_silently_skipping(self):
        with tempfile.TemporaryDirectory() as temporary:
            nunit = Path(temporary) / 'nunit.framework.dll'
            with self.assertRaisesRegex(ValueError, 'NUnit'):
                VERIFY.require_test_dependency([Path('Regression.cs')], nunit)
            VERIFY.require_test_dependency([], nunit)
            nunit.write_bytes(b'present dependency')
            VERIFY.require_test_dependency([Path('Regression.cs')], nunit)


class GitBaselineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='save verify ')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'repository with spaces'
        self.root.mkdir()
        self.git('init', '-q')
        self.git('config', 'user.name', 'Verification Tests')
        self.git('config', 'user.email', 'verification@example.invalid')
        self.write('Assets/Scripts/Runtime.cs', 'original runtime')
        self.write('Assets/Editor/Tests/Changed.cs', 'original fixture')
        self.write('Assets/Editor/Tests/Deleted.cs', 'deleted fixture')
        self.write('Assets/Editor/Tests/Untouched.cs', 'unchanged fixture')
        self.commit()
        self.base = self.git('rev-parse', 'HEAD').strip()
        self.git('tag', 'baseline')

    def git(self, *arguments):
        return subprocess.run(['git', *arguments], cwd=self.root, check=True,
                              capture_output=True, text=True).stdout

    def write(self, name, contents):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(contents)
        return target

    def commit(self):
        self.git('add', '.')
        self.git('commit', '-qm', 'test fixture')

    def selected(self):
        return [path.relative_to(self.root).as_posix()
                for path in VERIFY.changed_tests(self.root, self.base)]

    def test_clean_committed_changed_fixture_is_selected_against_base(self):
        self.write('Assets/Editor/Tests/Changed.cs', 'committed fix regression')
        self.commit()
        self.assertEqual('', self.git('status', '--porcelain'))
        self.assertEqual(['Assets/Editor/Tests/Changed.cs'], self.selected())

    def test_dirty_staged_untracked_and_deleted_paths_are_handled(self):
        self.write('Assets/Editor/Tests/Changed.cs', 'dirty fixture')
        self.write('Assets/Editor/Tests/Staged.cs', 'staged fixture')
        self.git('add', 'Assets/Editor/Tests/Staged.cs')
        self.write('Assets/Editor/Tests/New space\nfixture.cs', 'untracked fixture')
        (self.root / 'Assets/Editor/Tests/Deleted.cs').unlink()
        self.write('Assets/Editor/Tests/DeckCollectionTests.cs', 'native-only fixture')
        self.write('Assets/Scripts/NotATest.cs', 'not an EditMode fixture')
        self.assertEqual([
            'Assets/Editor/Tests/Changed.cs',
            'Assets/Editor/Tests/New space\nfixture.cs',
            'Assets/Editor/Tests/Staged.cs',
        ], self.selected())

    def test_archive_uses_resolved_commit_after_base_reference_moves(self):
        resolved = VERIFY.resolve_base(self.root, 'baseline')
        self.assertEqual(self.base, resolved)
        self.write('Assets/Scripts/Runtime.cs', 'changed runtime')
        self.commit()
        self.git('tag', '-f', 'baseline')
        destination = Path(self.temporary.name) / 'archive with spaces'
        VERIFY.archive_baseline(self.root, resolved, destination)
        self.assertEqual('original runtime',
                         (destination / 'Assets/Scripts/Runtime.cs').read_text())
        self.assertFalse((destination / 'Assets/Editor/Tests').exists())

    def test_non_commit_base_is_rejected(self):
        tree = self.git('rev-parse', 'HEAD^{tree}').strip()
        with self.assertRaisesRegex(ValueError, 'not a valid commit'):
            VERIFY.resolve_base(self.root, tree)


if __name__ == '__main__':
    unittest.main()
