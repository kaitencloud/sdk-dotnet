# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'scripts'))
spec = importlib.util.spec_from_file_location('review_generator', ROOT/'scripts/generate-management.py')
generator = importlib.util.module_from_spec(spec); spec.loader.exec_module(generator)


class Phase1ReviewGeneration(unittest.TestCase):
    def setUp(self):
        generator.MODELS.clear(); generator.ENUMS.clear()

    def test_required_query_flags_survive_model_api_and_minimal_fixture(self):
        files, rows = generator.render()
        row = next(row for row in rows if row['operationId']=='get-entitlement-group-usage')
        self.assertTrue(row['queryParameters'][0]['required'])
        self.assertIn('public required string Instance', files['Models/GetUsageEntitlementGroupQuery.g.cs'])
        self.assertNotIn('GetUsageEntitlementGroupQuery? query = null', files['EntitlementGroups.g.cs'])
        fixture = next(f for f in json.loads((ROOT/'tests/KaitenCloud.Sdk.Tests/Fixtures/management-wire.json').read_text()) if f['operationId']=='get-entitlement-group-usage' and not f['full'])
        self.assertIn('instance', fixture['query'])

    def test_typed_map_has_null_member_guard_but_arbitrary_json_slots_do_not(self):
        files, _ = generator.render()
        self.assertIn('Integrations.Value.Values.Any(item => item is null)', files['Models/CreateCustomerRequest.g.cs'])
        self.assertNotIn('Metadata.Value.Values.Any(item => item is null)', files['Models/CustomerIntegrationInput.g.cs'])

    def test_unsupported_root_compositions_fail_actual_generation_without_partial_files(self):
        for keyword in ['oneOf','allOf','anyOf']:
            with self.subTest(keyword=keyword), tempfile.TemporaryDirectory() as temporary:
                generator.MODELS.clear(); generator.ENUMS.clear()
                documents = copy.deepcopy(generator.load_contracts())
                schema = {keyword:[{'type':'object','properties':{'requiredValue':{'type':'string'}},'required':['requiredValue']}]}
                if keyword != 'allOf': schema[keyword].append({'type':'object','properties':{'other':{'type':'string'}}})
                documents['core']['paths']['/customers']['post']['requestBody']['content']['application/json']['schema'] = schema
                root = Path(temporary); (root/'contracts').mkdir()
                (root/'contracts/batch1.json').write_bytes((ROOT/'contracts/batch1.json').read_bytes())
                with patch.object(generator, 'load_contracts', return_value=documents), patch.object(generator,'ROOT',root), patch.object(sys,'argv',['generate-management.py']):
                    with self.assertRaisesRegex(ValueError, 'Unsupported.*composition'):
                        generator.main()
                self.assertFalse((root/'src').exists(), 'No partial generated files may be written on a schema failure')

    def test_required_query_mutation_changes_both_api_and_minimal_fixture(self):
        fixture_spec = importlib.util.spec_from_file_location('review_fixtures', ROOT/'scripts/generate-management-fixtures.py')
        fixtures = importlib.util.module_from_spec(fixture_spec); fixture_spec.loader.exec_module(fixtures)
        documents = copy.deepcopy(generator.load_contracts())
        operation = documents['core']['paths']['/entitlement-groups/{entitlementGroupSlug}/usage']['get']
        parameter = next(p for p in operation['parameters'] if p['in']=='query')
        self.assertIn('instance', fixtures.query_values(operation, False))
        parameter['required'] = False
        self.assertIsNone(fixtures.query_values(operation, False))
        with patch.object(generator, 'load_contracts', return_value=documents):
            files, rows = generator.render()
        row = next(row for row in rows if row['operationId']=='get-entitlement-group-usage')
        self.assertFalse(row['queryParameters'][0]['required'])
        self.assertIn('Optional<string> Instance', files['Models/GetUsageEntitlementGroupQuery.g.cs'])
        self.assertIn('GetUsageEntitlementGroupQuery? query = null', files['EntitlementGroups.g.cs'])

    def test_nullable_typed_map_and_json_null_entries_keep_their_contract(self):
        files, _ = generator.render()
        self.assertNotIn('Metadata.Values.Any(item => item is null)', files['Models/UpdateFeatureFlagRequest.g.cs'])
        for keyword in ['oneOf','anyOf']:
            with self.subTest(keyword=keyword):
                schema = {'type':'object','properties':{'entries':{'type':'object','additionalProperties':{keyword:[{'$ref':'#/components/schemas/CustomerIntegration'},{'type':'null'}]}}}}
                output = generator.model('NullableEntries', schema, True)
                self.assertNotIn('Values.Any(item => item is null)', output)
                self.assertIn('CustomerIntegrationInput?', output)
                tagged = {'type':'object','properties':{'entries':{'type':'object','additionalProperties':{keyword:[{'$ref':'#/components/schemas/NumberEntitlementValue'},{'$ref':'#/components/schemas/BooleanEntitlementValue'},{'$ref':'#/components/schemas/ConfigEntitlementValue'},{'type':'null'}], 'discriminator':{'propertyName':'type','mapping':{'number':'#/components/schemas/NumberEntitlementValue','boolean':'#/components/schemas/BooleanEntitlementValue','object':'#/components/schemas/ConfigEntitlementValue'}}}}}}
                output = generator.model('NullableTaggedEntries', tagged, True)
                self.assertNotIn('Values.Any(item => item is null)', output)

    def test_root_items_guard_comes_from_schema_nullability(self):
        generator.render()
        self.assertTrue(generator.nonnullable_root_items({'type':'array','items':{'$ref':'#/components/schemas/EntitlementUsage'}}, 'Usage'))
        for keyword in ['oneOf','anyOf']:
            with self.subTest(keyword=keyword):
                self.assertFalse(generator.nonnullable_root_items({'type':'array','items':{keyword:[{'$ref':'#/components/schemas/EntitlementUsage'},{'type':'null'}]}}, 'NullableUsage'))
                self.assertTrue(generator.nonnullable_root_items({'type':'array','items':{keyword:[{'$ref':'#/components/schemas/EntitlementUsage'}]}}, 'NonnullableUsage'))
        self.assertTrue(generator.nullable({'type':['object','null']}))
        self.assertFalse(generator.nullable({'type':'object'}))
        self.assertFalse(generator.nullable({'allOf':[{'type':'null'}]}))

    def test_both_alternative_forms_drive_actual_typed_map_and_operation_policy(self):
        for keyword in ['oneOf','anyOf']:
            for allows_null in [False, True]:
                with self.subTest(keyword=keyword, nullable=allows_null):
                    generator.MODELS.clear(); generator.ENUMS.clear()
                    documents = copy.deepcopy(generator.load_contracts())
                    null_branch = [{'type':'null'}] if allows_null else []
                    documents['core']['components']['schemas']['Customer']['properties']['integrations']['additionalProperties'] = {keyword:[{'$ref':'#/components/schemas/CustomerIntegration'}] + null_branch}
                    operation = documents['core']['paths']['/instances/{instanceSlug}/entitlements/usage']['get']
                    operation['responses']['200']['content']['application/json']['schema']['items'] = {keyword:[{'$ref':'#/components/schemas/EntitlementUsage'}] + null_branch}
                    with patch.object(generator, 'load_contracts', return_value=documents):
                        files, rows = generator.render()
                    request = files['Models/CreateCustomerRequest.g.cs']
                    self.assertEqual(not allows_null, 'Values.Any(item => item is null)' in request)
                    self.assertEqual(allows_null, 'CustomerIntegrationInput?' in request)
                    row = next(row for row in rows if row['operationId']=='getEntitlementsUsageMetrics')
                    self.assertEqual(allows_null, 'EntitlementUsage?' in row['responseType'])
                    policy = next(line for line in files['Operations.g.cs'].splitlines() if 'getEntitlementsUsageMetrics' in line)
                    self.assertTrue(policy.endswith(', false);' if allows_null else ', true);'), policy)

    def test_referenced_composed_model_cannot_silently_drop_required_fields(self):
        documents = copy.deepcopy(generator.load_contracts())
        documents['core']['components']['schemas']['CustomerIntegration']['allOf'] = [{'type':'object','properties':{'additionalRequired':{'type':'string'}},'required':['additionalRequired']}]
        with patch.object(generator, 'load_contracts', return_value=documents):
            with self.assertRaisesRegex(ValueError, 'Unsupported.*composition'):
                generator.render()


if __name__ == '__main__': unittest.main()
