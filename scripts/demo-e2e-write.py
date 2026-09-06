#!/usr/bin/env python3
"""Staging-only FeatureServer serving certification; invoked by run-demo-e2e.sh."""
import json
import os
from pathlib import Path
import re
import signal
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid


class CheckFailed(Exception):
    pass


def require(condition, message):
    if not condition:
        raise CheckFailed(message)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Harness:
    def __init__(self):
        self.base = os.environ['WRITE_BASE_URL']
        self.key = os.environ['ADMIN_KEY']
        self.readonly = os.environ.get('HONUA_DEMO_READ_ONLY_KEY', '')
        service = os.environ.get('HONUA_DEMO_WRITE_SERVICE_ID', os.environ['DEMO_SERVICE_ID'])
        layer = os.environ.get('HONUA_DEMO_WRITE_LAYER_ID', os.environ['DEMO_LAYER_ID'])
        self.url = f'{self.base}/rest/services/{urllib.parse.quote(service, safe="")}/FeatureServer/{urllib.parse.quote(layer, safe="")}'
        self.field = os.environ.get('HONUA_DEMO_WRITE_MARKER_FIELD', 'name')
        require(re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', self.field), 'Invalid write marker field')
        # A run owns only its nonce, even when callers reuse the resource prefix.
        self.marker = os.environ['RESOURCE_PREFIX'] + '-' + uuid.uuid4().hex
        self.timeout = float(os.environ['TIMEOUT_SECONDS'])
        self.opener = urllib.request.build_opener(NoRedirect())
        self.hops = []
        self.armed = False
        self.oid_field = None
        self.fixture = {'attributes': {self.field: self.marker},
                        'geometry': {'x': -156.5, 'y': 20.8, 'spatialReference': {'wkid': 4326}}}

    def record(self, name, values=None, error=None, pending=None):
        self.hops.append({'id': name, 'workflow': 'Demo B' if name.startswith('demoB') else 'Demo A',
                          'status': 'PENDING' if pending else 'FAIL' if error else 'PASS',
                          'driver': 'http', 'detail': pending or error or json.dumps(values, sort_keys=True),
                          'asserted_values': values or {}})

    def request(self, path, payload=None, key=None):
        headers = {'Accept': 'application/json'}
        if key:
            headers['X-API-Key'] = key
        data = None
        if payload is not None:
            data = json.dumps(payload).encode()
            headers['Content-Type'] = 'application/json'
        req = urllib.request.Request(self.url + path, data=data, headers=headers)
        try:
            response = self.opener.open(req, timeout=self.timeout)
        except urllib.error.HTTPError as exc:
            response = exc
        except (OSError, urllib.error.URLError):
            raise CheckFailed('HTTP transport failure') from None
        with response:
            body = response.read()
            status = response.status
        try:
            parsed = json.loads(body) if body else None
        except (ValueError, UnicodeDecodeError):
            raise CheckFailed(f'HTTP {status}: invalid JSON') from None
        return status, body, parsed

    def ok(self, path, payload=None):
        status, _, data = self.request(path, payload, self.key)
        require(status == 200 and isinstance(data, dict) and 'error' not in data,
                f'Expected successful JSON response, HTTP {status}')
        return data

    def query(self):
        where = f"{self.field} = '{self.marker.replace(chr(39), chr(39)*2)}'"
        data = self.ok('/query?' + urllib.parse.urlencode({
            'where': where, 'outFields': '*', 'returnGeometry': 'true',
            'outSR': '4326', 'f': 'json'}))
        require(isinstance(data.get('features'), list) and not data.get('exceededTransferLimit'),
                'Expected complete features array')
        return data['features']

    def edits(self, payload, kind, count):
        data = self.ok('/applyEdits', dict(payload, rollbackOnFailure=True))
        results = data.get(kind)
        require(isinstance(results, list) and len(results) == count
                and all(r.get('success') is True for r in results), f'{kind} did not confirm {count} successful edit(s)')
        return results

    def denial(self, name, key, expected):
        # Arm cleanup BEFORE even an unauthorized request: a broken target may accept it.
        self.armed = True
        status, body, data = self.request('/applyEdits', {'adds': [self.fixture], 'rollbackOnFailure': True}, key)
        rows = self.query()
        # Transport refusal with a literally empty body, or Honua's documented
        # GeoServices HTTP 200 error-only envelope. No feature/edit payload allowed.
        empty = not body
        envelope = (isinstance(data, dict) and set(data) == {'error'}
                    and isinstance(data['error'], dict) and data['error'].get('code') == expected
                    and set(data['error']) <= {'code', 'message', 'details'})
        require((status == expected and empty) or (status == 200 and envelope),
                f'Expected empty HTTP {expected} or HTTP 200 error-only code {expected}; got HTTP {status}')
        require(rows == [], 'Denied write created records')
        return {'http_status': status, 'refusal_code': expected, 'body_bytes': len(body),
                'body_kind': 'empty' if empty else 'error-only', 'records': 0, 'features': []}

    def run(self):
        def interrupted(signum, frame):
            raise CheckFailed('Write run interrupted')
        signal.signal(signal.SIGTERM, interrupted)
        signal.signal(signal.SIGINT, interrupted)
        stage = 'demoA.write_preflight'
        try:
            meta = self.ok('?f=json')
            fields = meta.get('fields', [])
            require(any(f.get('name') == self.field and f.get('type') == 'esriFieldTypeString' for f in fields),
                    'Write layer requires the configured string marker field')
            oid_fields = [f['name'] for f in fields if f.get('type') == 'esriFieldTypeOID']
            self.oid_field = meta.get('objectIdField') or meta.get('objectIdFieldName') or (oid_fields[0] if oid_fields else None)
            require(self.oid_field, 'Write layer has no object ID field')
            require(self.query() == [], 'Fresh fixture namespace is not empty')
            self.record(stage, {'records': 0, 'marker_field': self.field})
            stage = 'auth.no_key'
            self.record(stage, self.denial(stage, None, 401))
            if self.readonly:
                stage = 'auth.read_only'
                self.record(stage, self.denial(stage, self.readonly, 403))
            else:
                self.record('auth.read_only', pending='HONUA_DEMO_READ_ONLY_KEY not configured')
            stage = 'demoA.import'
            results = self.edits({'adds': [self.fixture]}, 'addResults', 1)
            oid = results[0].get('objectId')
            require(type(oid) is int, 'Import response has no integer objectId')
            rows = self.query()
            require(len(rows) == 1, 'Import did not read back exactly one record')
            attrs, geometry = rows[0].get('attributes', {}), rows[0].get('geometry', {})
            require(attrs.get(self.field) == self.marker and attrs.get(self.oid_field) == oid,
                    'Imported attributes differ from submitted values')
            require(abs(geometry.get('x', 0) + 156.5) < 1e-7 and abs(geometry.get('y', 0) - 20.8) < 1e-7,
                    'Imported geometry differs from submitted values')
            self.record(stage, {'records': 1, 'object_id': oid, 'attributes': {self.field: self.marker},
                                'x': geometry['x'], 'y': geometry['y']})
            stage = 'demoB.rollback'
            before = self.ok('?f=json')
            # Valid change first, followed by an invalid update missing its OID.
            # Both must fail with rollbackOnFailure=true, and actual data must survive.
            result = self.ok('/applyEdits', {'updates': [
                {'attributes': {self.oid_field: oid, self.field: self.marker + '-changed'}},
                {'attributes': {self.field: self.marker}}], 'rollbackOnFailure': True})
            updates = result.get('updateResults')
            require(isinstance(updates, list) and len(updates) == 2
                    and all(r.get('success') is False and isinstance(r.get('error'), dict) for r in updates),
                    'Rollback did not reject both edits')
            require(any(r['error'].get('code') == 1008 for r in updates), 'Missing operation-rolled-back error 1008')
            require(self.ok('?f=json') == before, 'Rollback changed layer schema/metadata')
            require(self.query() == rows, 'Rollback failed to restore fixture data')
            self.record(stage, {'rollback_on_failure': True, 'rollback_error_code': 1008,
                                'failed_edits': 2, 'schema_equal': True, 'data_equal': True, 'records': 1})
        except CheckFailed as exc:
            self.record(stage, error=str(exc))
        finally:
            if self.armed:
                self.cleanup()
        return self.hops

    def cleanup(self):
        try:
            # The marker survives accepted denial writes and lost create responses.
            # Include a failed rollback's changed marker without touching other runs.
            where = f"{self.field} IN ('{self.marker.replace(chr(39), chr(39)*2)}', '{(self.marker + '-changed').replace(chr(39), chr(39)*2)}')"
            def remaining():
                data = self.ok('/query?' + urllib.parse.urlencode({'where': where, 'outFields': '*', 'f': 'json'}))
                require(isinstance(data.get('features'), list) and not data.get('exceededTransferLimit'),
                        'Cleanup query did not return a complete features array')
                return data['features']
            rows = remaining()
            if rows:
                ids = [r.get('attributes', {}).get(self.oid_field) for r in rows]
                require(all(type(i) is int for i in ids), 'Cleanup requires integer fixture IDs')
                self.edits({'deletes': ids}, 'deleteResults', len(ids))
            require(remaining() == [], 'Cleanup left fixture records behind')
            self.record('demoA.cleanup', {'deleted_records': len(rows), 'records': 0, 'features': []})
        except CheckFailed as exc:
            self.record('demoA.cleanup', error=str(exc))


if __name__ == '__main__':
    try:
        hops = Harness().run()
    except (CheckFailed, ValueError, KeyError):
        hops = [{'id': 'demoA.write_preflight', 'workflow': 'Demo A', 'status': 'FAIL',
                 'driver': 'http', 'detail': 'Invalid write configuration', 'asserted_values': {}}]
    Path(sys.argv[1]).write_text(json.dumps(hops, indent=2) + '\n')
