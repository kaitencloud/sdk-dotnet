#!/usr/bin/env python3
# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
"""Schema-shaped golden HTTP fixtures; never runs an SDK or a real endpoint."""
import json
from pathlib import Path
import sys
sys.dont_write_bytecode = True
from check_contracts import ROOT, load_contracts

DOC = load_contracts()['core']
SCHEMAS = DOC['components']['schemas']


def sample(schema, full, request, trail=()):
    if '$ref' in schema:
        name = schema['$ref'].split('/')[-1]
        if name in trail:
            return None
        if name == 'LicenseTerm':
            return {'status':'ACTIVE', 'evaluatedAt':'2026-11-01T00:00:00Z'}
        return sample(SCHEMAS[name], full, request, trail + (name,))
    choices = schema.get('oneOf', schema.get('anyOf', []))
    if choices:
        real = [s for s in choices if s.get('type') != 'null']
        return sample(real[0], full, request, trail)
    t = schema.get('type', None)
    null = isinstance(t, list) and 'null' in t
    if isinstance(t, list):
        t = next(s for s in t if s != 'null')
    if t == 'object':
        if 'properties' not in schema:
            child = schema.get('additionalProperties', {})
            return {'sample': sample(child if isinstance(child, dict) else {}, full, request, trail)}
        properties = schema['properties']
        result = {}
        for key, value in properties.items():
            if key == 'licenseTerm' and trail and trail[-1] == 'Instance':
                continue  # Computed detail-only; list/create/nested resource fixtures omit it.
            if request and value.get('readOnly') or not request and value.get('writeOnly'):
                continue
            if key not in schema.get('required', []) and not full:
                continue
            if '$ref' in value and value['$ref'].split('/')[-1] in trail:
                continue
            result[key] = sample(value, full, request, trail)
        if 'items' in properties and 'hasMore' in properties:
            result['hasMore'] = False
            result.pop('nextCursor', None)
        if 'entitlementSlug' in result:
            result['entitlementSlug'] = 'entitlement-slug'
        return result
    if t == 'array':
        if '$ref' in schema['items'] and schema['items']['$ref'].split('/')[-1] in trail:
            return []
        return [sample(schema['items'], full, request, trail)]
    if null:
        return None
    if schema.get('enum'):
        return next(v for v in schema['enum'] if v is not None)
    if t == 'boolean':
        return False
    if t == 'integer':
        return max(schema.get('minimum', 1), 1)
    if t == 'number':
        return max(schema.get('minimum', 1), 1)
    if t == 'string' and schema.get('format') == 'date-time':
        return '2026-10-01T05:30:00+05:30'
    if t == 'string' and schema.get('format') == 'uuid':
        return 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
    if t == 'string':
        if schema.get('examples'):
            return schema['examples'][0]
        return 'example'
    if t is None:
        return True if trail and trail[-1] == 'Variant' else {'example':42}
    raise ValueError(f'Unsupported fixture schema: {schema}')


def query_values(operation, full):
    values = {p['name']: sample(p['schema'], True, True)
              for p in operation.get('parameters', [])
              if p['in']=='query' and (full or p.get('required', False))}
    return values or None


def render():
    rows = []
    for api in json.loads((ROOT/'contracts/batch1-api.json').read_text()):
        operation = DOC['paths'][api['path']][api['method'].lower()]
        for full in [False, True]:
            body_schema = operation.get('requestBody', {}).get('content', {}).get('application/json', {}).get('schema')
            response = operation['responses'][str(api['status'])].get('content', {}).get('application/json', {}).get('schema')
            path_params = {p['name']: ('external:42' if p['name']=='externalId' else 'sample.adapter' if p['name'] in ['adapter','integrationName'] else p['name'].replace('Slug','-slug').lower()) for p in api['parameters']}
            if 'instanceSlug' in path_params: path_params['instanceSlug']='instance-slug'
            if 'entitlementSlug' in path_params: path_params['entitlementSlug']='entitlement-slug'
            body = sample(body_schema, full, True) if body_schema else None
            payload = sample(response, full, False) if response else None
            if api['operationId'] == 'getInstance':
                payload['slug'] = path_params['instanceSlug']
                payload['licenseTerm'] = sample({'$ref':'#/components/schemas/LicenseTerm'}, full, False)
            if isinstance(payload, dict): payload['futureProperty'] = {'retained':True}
            query = query_values(operation, full)
            rows.append({'operationId':api['operationId'],'full':full,'pathArguments':path_params,'request':body,'query':query,'response':payload})
    return json.dumps(rows,indent=2)+'\n'


def main():
    output = render()
    path = ROOT/'tests/KaitenCloud.Sdk.Tests/Fixtures/management-wire.json'
    if '--check' in sys.argv:
        if path.read_text()!=output: raise ValueError('Wire fixture drift')
    else:
        path.parent.mkdir(parents=True,exist_ok=True);path.write_text(output)
    print(f'PASS: {len(json.loads(output))} full/minimal schema-shaped wire fixtures')


if __name__ == '__main__':
    main()
