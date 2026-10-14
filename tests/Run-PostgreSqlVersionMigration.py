import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import uuid

root = Path(__file__).resolve().parents[1]
old = int(sys.argv[1]) if len(sys.argv) > 1 else 14
if old not in (14, 16):
    raise SystemExit('Only 14/17 and 16/17 fixture pairs are defined')
output = Path(sys.argv[2]) if len(sys.argv) > 2 else Path('/tmp') / ('mysqlpunk-pg-version-' + uuid.uuid4().hex)
output.mkdir(parents=True, exist_ok=False)
project = root / 'tests/PostgreSqlVersionMigrationHarness/Review.csproj'
subprocess.run(['dotnet', 'build', str(project), '-c', 'Release'], check=True)
key = 'mysqlpunk-pg-version-' + uuid.uuid4().hex
environment = os.environ.copy()
environment['POSTGRES_PASSWORD'] = 'Aa!' + secrets.token_hex(18)
environment['MYSQLPUNK_PG_TEST_PASSWORD'] = environment['POSTGRES_PASSWORD']
environment['MYSQLPUNK_OLD_PG_MAJOR'] = str(old)
containers = []
metadata = {'sources': {}, 'images': [], 'cleanup': []}
for path in [project, *project.parent.glob('*.cs'), Path(__file__).resolve(),
             *sorted((root / 'mySQLPunk.Core').rglob('*.cs'))]:
    if 'obj' not in path.parts and 'bin' not in path.parts:
        metadata['sources'][str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()

def docker(*arguments):
    result = subprocess.run(['docker', *arguments], env=environment, text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError('Docker operation failed: ' + arguments[0])
    return result.stdout.strip()

try:
    ports = []
    for major in (old, 17):
        image = json.loads(docker('image', 'inspect', f'postgres:{major}-alpine', '--format', '{{json .}}'))
        container = docker('run', '-d', '--name', key + '-' + str(major),
                           '--label', 'mysqlpunk.test.owner=' + key, '--cpus=1', '--memory=768m',
                           '--pids-limit=512', '--mount', 'type=tmpfs,destination=/var/lib/postgresql/data,tmpfs-size=536870912',
                           '-e', 'POSTGRES_PASSWORD', '-p', '127.0.0.1::5432', image['Id'])
        containers.append(container)
        ports.append(docker('port', container, '5432/tcp').split(':')[-1])
        metadata['images'].append({'major': major, 'id': image['Id'], 'digests': image.get('RepoDigests', [])})
    command = ['dotnet', str(project.parent / 'bin/Release/net8.0/Review.dll'), *ports]
    for attempt in range(30):
        probe = subprocess.run(command + ['probe'], env=environment, text=True, capture_output=True, timeout=15)
        if probe.returncode == 0:
            metadata['versions'] = probe.stdout.strip().splitlines()
            print('Actual versions: ' + ' / '.join(metadata['versions']), flush=True)
            break
        time.sleep(2)
    else:
        raise RuntimeError('Isolated PostgreSQL engines did not become ready')
    result = subprocess.run(command, env=environment, text=True, capture_output=True, timeout=180)
    text = (result.stdout + '\n' + result.stderr).replace(environment['POSTGRES_PASSWORD'], '[redacted]')
    (output / 'migration.log').write_text(text)
    print(text, flush=True)
    if result.returncode:
        raise RuntimeError('Formal Core project migration regression failed')
finally:
    failures = []
    for container in containers:
        try:
            if docker('inspect', container, '--format', '{{index .Config.Labels "mysqlpunk.test.owner"}}') != key:
                raise RuntimeError('Refusing cleanup: ownership mismatch')
            docker('rm', '-f', container)
            metadata['cleanup'].append(container)
        except Exception as exception:
            failures.append({'container': container, 'error': str(exception)})
    metadata['cleanup_failures'] = failures
    (output / 'evidence.json').write_text(json.dumps(metadata, indent=2) + '\n')
    print('Evidence: ' + str(output), flush=True)
    if failures:
        raise RuntimeError('Owned fixture cleanup incomplete; see evidence.json')
