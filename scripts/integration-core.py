#!/usr/bin/env python3
# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
"""Run against a NEW owned local Core; never rebuild/update an existing stack.
Supply already-built images of the instance-detail licenseTerm/UTC-corrected Core.
Only this invocation's containers, volumes and private credentials are removed.
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import time
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--core-source', type=Path, required=True)
for name in ('api', 'seeder', 'admin', 'bootstrap'):
    parser.add_argument('--' + name + '-image', required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
core = args.core_source.resolve()
if not (core / 'compose.yml').is_file():
    parser.error('core-source must contain the verified Core compose.yml')
project = 'kaiten-sdk-ofrep-' + secrets.token_hex(6)
state = root / '.local' / project
state.mkdir(parents=True, mode=0o700)
state.chmod(0o700)
dev = state / 'dev'
dev.mkdir(mode=0o700)

def private_file(path, content):
    with open(path, 'x', opener=lambda p, f: os.open(p, f, 0o600)) as output:
        output.write(content)

# Pin local image IDs. Never pull/build or retag anything the live Demo uses.
images = {}
for name in ('api', 'seeder', 'admin', 'bootstrap'):
    images[name] = subprocess.check_output(['docker', 'image', 'inspect', getattr(args, name + '_image'), '--format', '{{.Id}}'], text=True).strip()
# Only the Envoy and Jaeger endpoints are published; all others stay on the NEW network.
ports = []
for _ in range(2):
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        ports.append(probe.getsockname()[1])
gateway, jaeger = ports
values = {
    'KAITEN_CONTAINER_PREFIX': project, 'KAITEN_PORT': '16060', 'KAITEN_PLATFORM_PORT': '16001',
    'KAITEN_DATABASE_USER': 'kaiten', 'KAITEN_DATABASE_PASSWORD': secrets.token_hex(24), 'KAITEN_DATABASE': 'kaiten',
    'KAITEN_DEV_JWT_SECRET': secrets.token_hex(32), 'KAITEN_DEV_DIR': str(dev),
    'KAITEN_DEV_UID': str(os.getuid()), 'KAITEN_DEV_GID': str(os.getgid()),
    'KAITEN_APP_URL': 'http://127.0.0.1:15173', 'KAITEN_APP_PORT': '13000',
}
private_file(state / 'core.env', ''.join(k + '=' + v + '\n' for k, v in values.items()))
volume = project + '-postgres'
overlay = 'services:\n'
for name, image in [('api', images['api']), ('migrate', images['admin']), ('bootstrap', images['bootstrap']),
                    ('seed-accounts', images['seeder']), ('tokens', images['seeder']), ('seed-data', images['seeder'])]:
    overlay += f'  {name}:\n    image: {image}\n'
    if name == 'api':
        overlay += '    ports: !reset []\n'
overlay += f'''  envoy:
    ports: !override ["127.0.0.1:{gateway}:80"]
  jaeger:
    ports: !override ["127.0.0.1:{jaeger}:16686"]
  db:
    ports: !reset []
    volumes: !override ["sdk_pgdata:/var/lib/postgresql/data"]
  rabbitmq:
    ports: !reset []
volumes:
  sdk_pgdata:
    external: true
    name: {volume}
'''
private_file(state / 'owned.compose.yml', overlay)
compose = ['docker', 'compose', '--project-name', project, '--env-file', str(state / 'core.env'),
           '-f', str(core / 'compose.yml'), '-f', str(state / 'owned.compose.yml')]
log_file = state / 'bootstrap.log'
private_file(log_file, '')

def run(command, timeout=180):
    with log_file.open('a') as output:
        subprocess.run(command, cwd=root, stdout=output, stderr=subprocess.STDOUT, check=True, timeout=timeout)

def tool(*command):
    # migrate is scratch: explicit UID + mount for atomic 0600 credential writes.
    run(compose + ['run', '--rm', '--no-deps', '--user', f'{os.getuid()}:{os.getgid()}',
                   '--volume', str(dev) + ':/credentials', 'migrate'] + list(command))

volume_created = False
try:
    run(['docker', 'volume', 'create', '--label', 'kaiten.sdk.owner=' + project, volume])
    volume_created = True
    run(compose + ['--profile', 'accounts', 'up', '-d', '--no-build', '--pull', 'never'], timeout=300)
    base = f'http://127.0.0.1:{gateway}/api'
    for attempt in range(60):
        try:
            with urllib.request.urlopen(base + '/healthz', timeout=2) as response:
                if response.status == 200:
                    break
        except (OSError, TimeoutError):
            pass
        time.sleep(0.5)
    else:
        raise RuntimeError('New Core gateway did not become ready')
    run(compose + ['run', '--rm', '--no-deps', 'seed-data'], timeout=300)
    runtime_scopes = 'read:feature_flags,read:instances,write:instances,read:licenses,read:customers,read:entitlements'
    presenter_scopes = runtime_scopes + ',write:feature_flags,write:licenses'
    for name, scopes in [('runtime', runtime_scopes), ('presenter', presenter_scopes), ('under-scoped', 'read:customers')]:
        tool('service-token', 'mint', '--org', 'seed-kaiten-sushi-shop', '--name', 'sdk-' + name,
             '--scopes', scopes, '--output-file', '/credentials/' + name + '-token')
    tool('organization', 'ensure', '--external-id', project + '-foreign', '--name', 'SDK foreign test')
    tool('service-token', 'mint', '--org', project + '-foreign', '--name', 'sdk-foreign', '--scopes', runtime_scopes,
         '--output-file', '/credentials/foreign-token')
    receipt = {'project': project, 'volume': volume, 'images': images, 'gateway': base,
               'jaeger': f'http://127.0.0.1:{jaeger}', 'source': str(core)}
    private_file(state / 'environment.json', json.dumps(receipt, indent=2))
    env = dict(os.environ, KAITEN_BASE_URL=base, KAITEN_TEST_TOKEN_DIR=str(dev), KAITEN_JAEGER_URL=receipt['jaeger'],
               KAITEN_TEST_MUTATIONS='1', KAITEN_TEST_ENVIRONMENT=project, KAITEN_TEST_SNAPSHOT_DIR=str(state / 'snapshots'))
    result = subprocess.run(['dotnet', 'run', '--project', 'tests/KaitenCloud.Sdk.Integration', '-c', 'Release',
                             '--no-build', '--no-restore'], cwd=root, env=env, timeout=240)
    if result.returncode:
        raise RuntimeError('Integration runner failed; inspect private snapshots and bootstrap.log')
finally:
    # Compose targets ONLY our random project/config. Remove anonymous CDC and project Rabbit volumes too.
    run(compose + ['--profile', '*', 'down', '--volumes', '--remove-orphans'], timeout=120)
    if volume_created:
        owner = subprocess.check_output(['docker', 'volume', 'inspect', volume, '--format', '{{index .Labels "kaiten.sdk.owner"}}'], text=True).strip()
        if owner != project:
            raise RuntimeError('Volume ownership changed; refusing deletion')
        run(['docker', 'volume', 'rm', volume])
    # Credentials/DB secrets from THIS invocation are not needed after its resources are gone.
    shutil.rmtree(dev)
    (state / 'core.env').unlink()
    print('Owned test resources and credentials removed; evidence:', state)
