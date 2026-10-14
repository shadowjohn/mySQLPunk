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
output = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('/tmp') / ('mysqlpunk-sql-version-' + uuid.uuid4().hex)
output.mkdir(parents=True, exist_ok=False)
project = root / 'tests/SqlServerVersionMigrationHarness/Review.csproj'
subprocess.run(['dotnet', 'build', str(project), '-c', 'Release'], check=True)
key = 'mysqlpunk-version-' + uuid.uuid4().hex
environment = os.environ.copy()
environment['MSSQL_SA_PASSWORD'] = 'Aa!' + secrets.token_hex(18)
environment['MYSQLPUNK_TEST_PASSWORD'] = environment['MSSQL_SA_PASSWORD']
containers = []
metadata = {'sources': {}, 'images': []}
for path in [*project.parent.glob('*.cs'), root / 'tests/SqlServerBatchInsertTests.cs', project, Path(__file__).resolve(), *[root / 'mySQLPunk/lib' / name for name in
        ('SqlServerBatchInsert.cs', 'SqlServerCompatibilityMigration.cs', 'SqlServerMigrationSession.cs',
         'SqlServerMigrationCatalogReader.cs', 'MssqlExport/MssqlOwnedSession.cs')]]:
    metadata['sources'][str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()

def docker(*arguments):
    result = subprocess.run(['docker', *arguments], env=environment, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError('Docker operation failed: ' + arguments[0])
    return result.stdout.strip()

try:
    ports = []
    for year in (2019, 2022):
        image = json.loads(docker('image', 'inspect', 'mcr.microsoft.com/mssql/server:' + str(year) + '-latest', '--format', '{{json .}}'))
        container = docker('run', '-d', '--name', key + '-' + str(year), '--label', 'mysqlpunk.test.owner=' + key,
                           '--cpus=2', '--memory=2g', '--pids-limit=512', '--tmpfs', '/var/opt/mssql:uid=10001,gid=0',
                           '-e', 'ACCEPT_EULA=Y', '-e', 'MSSQL_PID=Developer', '-e', 'MSSQL_SA_PASSWORD',
                           '-p', '127.0.0.1::1433', image['Id'])
        containers.append(container)
        ports.append(docker('port', container, '1433/tcp').split(':')[-1])
        metadata['images'].append({'year': year, 'id': image['Id'], 'digests': image.get('RepoDigests', [])})
    command = ['dotnet', str(project.parent / 'bin/Release/net8.0/Review.dll'), *ports]
    for attempt in range(60):
        probe = subprocess.run(command + ['probe'], env=environment, text=True, capture_output=True, timeout=15)
        if probe.returncode == 0:
            metadata['versions'] = probe.stdout.strip().splitlines()
            print('Actual versions: ' + ' / '.join(metadata['versions']), flush=True)
            break
        time.sleep(2)
    else:
        raise RuntimeError('Isolated SQL engines did not become ready')
    result = subprocess.run(command, env=environment, text=True, capture_output=True, timeout=180)
    text = (result.stdout + '\n' + result.stderr).replace(environment['MSSQL_SA_PASSWORD'], '[redacted]')
    (output / 'migration.log').write_text(text)
    print(text, flush=True)
    if result.returncode:
        raise RuntimeError('Formal-source migration regression failed')
finally:
    metadata['cleanup'] = []
    for container in containers:
        if docker('inspect', container, '--format', '{{index .Config.Labels "mysqlpunk.test.owner"}}') != key:
            raise RuntimeError('Refusing cleanup: ownership mismatch')
        docker('rm', '-f', container)
        metadata['cleanup'].append(container)
        print('Removed owned isolated container', flush=True)
    (output / 'evidence.json').write_text(json.dumps(metadata, indent=2) + '\n')
    print('Evidence: ' + str(output), flush=True)
