# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
import importlib.util
import json
from pathlib import Path
import sys
import unittest
sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
spec = importlib.util.spec_from_file_location('management_generator', Path(__file__).resolve().parents[1] / 'scripts/generate-management.py')
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class ManagementGeneration(unittest.TestCase):
    def setUp(self):
        generator.MODELS.clear()
        generator.ENUMS.clear()

    def test_generation_is_byte_identical_and_excludes_evaluations(self):
        first, rows = generator.render()
        generator.MODELS.clear(); generator.ENUMS.clear()
        second, again = generator.render()
        self.assertEqual(first, second)
        self.assertEqual(rows, again)
        self.assertEqual(64, len(rows))
        self.assertFalse(any('/ofrep/' in row['path'] for row in rows))
        self.assertFalse(any('EvaluateFlag' in text for text in first.values()))

    def test_instance_term_is_detail_only_and_never_a_write_field(self):
        files, rows = generator.render()
        self.assertFalse(any(row['operationId']=='getInstanceLicenseValidation' for row in rows))
        self.assertIn('LicenseTerm?', files['Models/Instance.g.cs'])
        self.assertNotIn('LicenseTerm', files['Models/CreateInstanceRequest.g.cs'])
        self.assertNotIn('LicenseTerm', files['Models/UpdateInstanceRequest.g.cs'])
        self.assertIn('result.LicenseTerm is null', files['Instances.g.cs'])
        fixtures = json.loads((Path(__file__).resolve().parent/'KaitenCloud.Sdk.Tests/Fixtures/management-wire.json').read_text())
        def contains_term(value):
            if isinstance(value, dict): return 'licenseTerm' in value or any(contains_term(v) for v in value.values())
            if isinstance(value, list): return any(contains_term(v) for v in value)
            return False
        for fixture in fixtures:
            self.assertEqual(fixture['operationId']=='getInstance', contains_term(fixture['response']))
            self.assertFalse(contains_term(fixture['request']))

    def test_assignment_instant_converters_are_response_property_specific(self):
        files, _ = generator.render()
        attribute = '[JsonConverter(typeof(global::KaitenCloud.Sdk.InstanceAssignmentInstantConverter))]'
        for path, text in files.items():
            expected = 2 if path in {'Models/Instance.g.cs', 'Models/InstanceIntegrationResource.g.cs'} else 0
            self.assertEqual(expected, text.count(attribute), path)
        self.assertEqual(('DateTimeOffset', False), generator.typename({'type':'string','format':'date-time'}, 'OrdinaryDate'))
        for keyword in ['oneOf', 'anyOf']:
            schema = {'type':'object','properties':{'startLicenseDate':{keyword:[{'type':'string','format':'date-time'},{'type':'null'}]}},'required':['startLicenseDate']}
            text = generator.model('Instance', schema, False)
            self.assertIn('public required DateTimeOffset? StartLicenseDate', text)
            self.assertIn(attribute, text)
            self.assertIn('Condition = JsonIgnoreCondition.Never', text)
            self.assertNotIn(attribute, generator.model('Instance', schema, True))

    def test_unknown_type_union_fails_instead_of_dictionary_fallback(self):
        with self.assertRaisesRegex(ValueError, 'Unsupported type union'):
            generator.typename({'type':['string','number']}, 'Unknown')

    def test_unknown_composition_fails(self):
        with self.assertRaisesRegex(ValueError, 'Unsupported composed schema'):
            generator.typename({'oneOf':[{'type':'string'},{'type':'number'}]}, 'Unknown')

    def test_closed_empty_object_is_not_an_arbitrary_body(self):
        with self.assertRaisesRegex(ValueError, 'explicit model'):
            generator.typename({'type':'object','additionalProperties':False}, 'Empty')

    def test_changed_tag_mapping_requires_review(self):
        with self.assertRaisesRegex(ValueError, 'mapping changed'):
            generator.typename({'oneOf':[{'type':'string'},{'type':'number'}], 'discriminator':{'propertyName':'type','mapping':{'boolean':'wrong','number':'wrong','object':'wrong'}}}, 'Changed')

    def test_required_null_and_optional_inputs_have_distinct_templates(self):
        files, _ = generator.render()
        flag = files['Models/UpdateFeatureFlagRequest.g.cs']
        self.assertIn('public required string? Description', flag)
        self.assertIn('public required IReadOnlyDictionary<string, JsonElement?>? Metadata', flag)
        self.assertIn('Condition = JsonIgnoreCondition.Never', flag)
        self.assertNotIn('public required string Id', files['Models/CreateCustomerRequest.g.cs'])
        self.assertIn('Optional<bool?> IsPublic', files['Models/UpdateLicenseFamilyRequest.g.cs'])


if __name__ == '__main__':
    unittest.main()
