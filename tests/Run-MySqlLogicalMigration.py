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
output = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('/tmp') / ('mysqlpunk-my-pg-' + uuid.uuid4().hex)
output.mkdir(parents=True, exist_ok=False)
mode = sys.argv[2] if len(sys.argv) > 2 else 'regular'
if mode not in ('regular', 'boundaries'):
    raise ValueError('Unknown fixture mode')
project = root / 'tests/MySqlLogicalMigrationHarness/Review.csproj'
subprocess.run(['dotnet', 'build', str(project), '-c', 'Release'], check=True)
environment = os.environ.copy()
password = 'Aa!' + secrets.token_hex(18)
environment.update(POSTGRES_PASSWORD=password, MYSQL_ROOT_PASSWORD=password, MARIADB_ROOT_PASSWORD=password, MYSQLPUNK_TEST_PASSWORD=password)
key = 'mysqlpunk-my-pg-' + uuid.uuid4().hex
metadata = {'images': [], 'sources': {}, 'cleanup': []}
containers = []
for path in [project, *project.parent.glob('*.cs'), Path(__file__).resolve(), *sorted((root / 'mySQLPunk.Core').rglob('*.cs'))]:
    if 'bin' not in path.parts and 'obj' not in path.parts:
        metadata['sources'][str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()

def docker(*arguments):
    result = subprocess.run(['docker', *arguments], env=environment, text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError('Docker operation failed: ' + arguments[0])
    return result.stdout.strip()

try:
    ports = []
    engines = [
        ('postgres:16-alpine', 5432, '/var/lib/postgresql/data', 'POSTGRES_PASSWORD'),
        ('mysql:8.4', 3306, '/var/lib/mysql', 'MYSQL_ROOT_PASSWORD'),
        ('mariadb:11.4', 3306, '/var/lib/mysql', 'MARIADB_ROOT_PASSWORD')
    ]
    if mode == 'boundaries':
        engines += [('mysql:8.4', 3306, '/var/lib/mysql', 'MYSQL_ROOT_PASSWORD'),
                    ('mariadb:11.4', 3306, '/var/lib/mysql', 'MARIADB_ROOT_PASSWORD')]
    for index, (tag, port, directory, variable) in enumerate(engines):
        image = json.loads(docker('image', 'inspect', tag, '--format', '{{json .}}'))
        options = [] if port == 5432 else ['--max-allowed-packet=' + ('65536' if index > 2 else '8388608')]
        container = docker('run', '-d', '--name', key + '-' + str(index), '--label', 'mysqlpunk.test.owner=' + key,
                           '--cpus=1', '--memory=1g', '--pids-limit=512', '--mount', 'type=tmpfs,destination=' + directory + ',tmpfs-size=536870912',
                           '-e', variable, '-p', '127.0.0.1::' + str(port), image['Id'], *options)
        containers.append(container)
        ports.append(docker('port', container, str(port) + '/tcp').split(':')[-1])
        metadata['images'].append({'tag': tag, 'id': image['Id'], 'digests': image.get('RepoDigests', [])})
    arguments = ports[:3] + (['boundaries', *ports[3:]] if mode == 'boundaries' else [])
    command = ['dotnet', str(project.parent / 'bin/Release/net8.0/MySqlPunk.MySqlLogicalMigrationHarness.dll'), *arguments]
    for attempt in range(60):
        probe = subprocess.run(command + ['probe'], env=environment, text=True, capture_output=True, timeout=20)
        if probe.returncode == 0:
            metadata['versions'] = probe.stdout.strip().splitlines()
            break
        time.sleep(2)
    else:
        raise RuntimeError('Isolated engines not ready')
    result = subprocess.run(command, env=environment, text=True, capture_output=True, timeout=480)
    text = (result.stdout + '\n' + result.stderr).replace(password, '[redacted]')
    (output / 'migration.log').write_text(text)
    print(text, flush=True)
    if result.returncode:
        raise RuntimeError('Formal migration harness failed')
finally:
    failures = []
    for container in containers:
        try:
            if docker('inspect', container, '--format', '{{index .Config.Labels "mysqlpunk.test.owner"}}') != key:
                raise RuntimeError('Ownership mismatch; cleanup refused')
            docker('rm', '-f', container)
            metadata['cleanup'].append(container)
        except Exception as exception:
            failures.append({'container': container, 'error': str(exception)})
    metadata['cleanup_failures'] = failures
    (output / 'evidence.json').write_text(json.dumps(metadata, indent=2) + '\n')
    print('Evidence: ' + str(output), flush=True)
    if failures:
        raise RuntimeError('Fixture cleanup incomplete; inspect evidence.json')
