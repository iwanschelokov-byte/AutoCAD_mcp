import socket, json

def send(method, params={}):
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(15)
    sock.connect(('localhost', 8081))
    req = json.dumps({'jsonrpc':'2.0','method':method,'params':params,'id':'1'}) + '\n'
    sock.sendall(req.encode())
    data = b''
    while True:
        chunk = sock.recv(65536)
        if not chunk: break
        data += chunk
        if b'\n' in data: break
    sock.close()
    r = json.loads(data)
    ok = 'result' in r
    err = r.get('error',{}).get('message','')[:80] if not ok else ''
    result = None
    if ok:
        try:
            result = json.loads(r['result']) if isinstance(r['result'], str) else r['result']
        except:
            result = r['result']
    return ok, result, err

results = {}

def test(name, method, params={}):
    ok, r, e = send(method, params)
    tag = 'OK' if ok else 'FAIL'
    msg = '' if ok else f' -> {e}'
    print(f'  [{tag}] {name}{msg}')
    results[name] = ok
    return ok, r

print('=' * 60)
print('  AutoCAD MCP Plugin - Full Test Suite (67 tools)')
print('=' * 60)

# === SYSTEM (4) ===
print('\n--- SYSTEM ---')
test('system_status', 'system_status')
test('list_methods', 'list_methods')
test('get_system_variable', 'get_system_variable', {'name': 'DIMTXT'})
test('set_system_variable', 'set_system_variable', {'name': 'DIMTXT', 'value': 200})

# === DRAWING (7) ===
print('\n--- DRAWING ---')
test('drawing_info', 'drawing_info')
test('execute_command', 'execute_command', {'command': 'REGEN'})
test('set_units', 'set_units', {'linear_units': 4, 'angular_units': 0})
test('purge_drawing', 'purge_drawing', {'__confirm': True})
# drawing_new, drawing_open, drawing_save tested separately

# === STYLES (4) ===
print('\n--- STYLES ---')
test('create_text_style', 'create_text_style', {'name': 'TestStyle', 'font': 'Arial', 'height': 0, 'set_current': True})
test('list_text_styles', 'list_text_styles')
test('create_dimension_style', 'create_dimension_style', {'name': 'TestDim', 'text_height': 150, 'arrow_size': 100, 'set_current': True})
test('list_dimension_styles', 'list_dimension_styles')

# === LAYERS (7) ===
print('\n--- LAYERS ---')
test('create_layer', 'create_layer', {'name': 'TestLayer', 'color': 1})
send('create_layer', {'name': 'RenameMe', 'color': 3})
send('create_layer', {'name': 'DeleteMe', 'color': 5})
test('list_layers', 'list_layers')
test('set_current_layer', 'set_current_layer', {'name': 'TestLayer'})
test('set_layer_properties', 'set_layer_properties', {'name': 'TestLayer', 'color': 2})
test('rename_layer', 'rename_layer', {'old_name': 'RenameMe', 'new_name': 'Renamed'})
test('delete_layer', 'delete_layer', {'name': 'DeleteMe', '__confirm': True})
send('set_current_layer', {'name': '0'})

# === ENTITY CREATION (11) ===
print('\n--- ENTITY CREATION ---')
ok, r = test('create_line', 'create_line', {'start': [0, 0], 'end': [5000, 0]})
line_h = r['id'] if ok else None

ok, r = test('create_circle', 'create_circle', {'center': [10000, 5000], 'radius': 2000})
circ_h = r['id'] if ok else None

ok, r = test('create_arc', 'create_arc', {'center': [5000, 5000], 'radius': 1500, 'start_angle': 0, 'end_angle': 180})
arc_h = r['id'] if ok else None

ok, r = test('create_polyline', 'create_polyline', {'points': [[0,10000],[3000,13000],[6000,10000]], 'closed': True})
poly_h = r['id'] if ok else None

ok, r = test('create_rectangle', 'create_rectangle', {'corner1': [15000, 0], 'corner2': [20000, 5000]})
rect_h = r['id'] if ok else None

test('create_ellipse', 'create_ellipse', {'center': [25000, 5000], 'major_radius': 3000, 'minor_radius': 1500})
test('create_text', 'create_text', {'text': 'Hello MCP', 'position': [0, -2000], 'height': 300})
test('create_mtext', 'create_mtext', {'text': 'Multi\\nLine', 'position': [10000, -2000], 'height': 200, 'width': 3000})
test('create_hatch', 'create_hatch', {'boundary': [[15000,0],[20000,0],[20000,5000],[15000,5000]], 'pattern': 'ANSI31', 'scale': 50})
test('create_spline', 'create_spline', {'points': [[0,18000],[2000,20000],[4000,18000],[6000,20000]]})
test('create_table', 'create_table', {'position': [25000, 18000], 'rows': 3, 'columns': 2, 'data': [['Name','Value'],['Width','20m'],['Height','12m']]})

# === ENTITY QUERY (5) ===
print('\n--- ENTITY QUERY ---')
test('list_entities', 'list_entities')
test('get_entity', 'get_entity', {'handle': line_h})
test('measure_distance', 'measure_distance', {'point1': [0, 0], 'point2': [5000, 0]})
test('measure_area', 'measure_area', {'handle': circ_h})
test('get_bounding_box', 'get_bounding_box', {'handle': circ_h})

# === ENTITY MODIFY (8 original) ===
print('\n--- ENTITY MODIFY ---')
ok, r = test('copy_entity', 'copy_entity', {'handle': circ_h, 'from': [10000,5000], 'to': [30000,5000]})
copy_h = r['id'] if ok and r else None

if copy_h:
    test('move_entity', 'move_entity', {'handle': copy_h, 'from': [30000,5000], 'to': [30000,6000]})
    test('rotate_entity', 'rotate_entity', {'handle': copy_h, 'base_point': [30000,6000], 'angle': 45})
    test('scale_entity', 'scale_entity', {'handle': copy_h, 'base_point': [30000,6000], 'factor': 0.8})
    test('mirror_entity', 'mirror_entity', {'handle': copy_h, 'mirror_line_start': [30000,0], 'mirror_line_end': [30000,20000]})
test('set_entity_properties', 'set_entity_properties', {'handle': line_h, 'color': 1})
if copy_h:
    test('erase_entity', 'erase_entity', {'handle': copy_h, '__confirm': True})

# === ADVANCED MODIFY (6 new) ===
print('\n--- ADVANCED MODIFY ---')
test('offset_entity', 'offset_entity', {'handle': line_h, 'distance': 500, 'side': 'both'})
test('explode_entity', 'explode_entity', {'handle': rect_h, 'erase_original': False})
test('array_rectangular', 'array_rectangular', {'handle': arc_h, 'rows': 2, 'columns': 2, 'row_spacing': 4000, 'column_spacing': 4000})
test('array_polar', 'array_polar', {'handle': arc_h, 'center': [5000, 5000], 'count': 4, 'angle': 360})
test('undo_last', 'undo_last')
# join needs 2+ handles - create two touching lines
ok1, r1, _ = send('create_line', {'start': [50000,0], 'end': [55000,0]})
ok2, r2, _ = send('create_line', {'start': [55000,0], 'end': [60000,0]})
jh1 = r1['id'] if ok1 and r1 else None
jh2 = r2['id'] if ok2 and r2 else None
if jh1 and jh2:
    test('join_entities', 'join_entities', {'handles': [jh1, jh2]})
else:
    print('  [SKIP] join_entities - could not create test lines')

# === SELECTION (2 new) ===
print('\n--- SELECTION ---')
test('select_by_properties', 'select_by_properties', {'layer': '0'})
test('select_by_window', 'select_by_window', {'min_point': [0, 0], 'max_point': [30000, 20000]})

# === ANNOTATIONS (6) ===
print('\n--- ANNOTATIONS ---')
test('create_linear_dimension', 'create_linear_dimension', {'point1': [0,0], 'point2': [5000,0], 'dimension_line_position': [2500,-1000]})
test('create_aligned_dimension', 'create_aligned_dimension', {'point1': [0,10000], 'point2': [6000,13000], 'dimension_line_position': [3000,12000]})
test('create_angular_dimension', 'create_angular_dimension', {'center': [0,0], 'point1': [5000,0], 'point2': [0,5000], 'dimension_arc_position': [3000,3000]})
test('create_radial_dimension', 'create_radial_dimension', {'center': [10000,5000], 'chord_point': [12000,5000], 'leader_length': 500})
test('create_diameter_dimension', 'create_diameter_dimension', {'center': [10000,5000], 'chord_point': [12000,5000], 'leader_length': 500})
test('create_leader', 'create_leader', {'points': [[5000,5000],[7000,7000]], 'text': 'Test Leader', 'text_height': 200})

# === BLOCKS (3) ===
print('\n--- BLOCKS ---')
# create_block needs existing entity handles, not raw coords
ok_bl, r_bl, _ = send('create_rectangle', {'corner1': [45000,0], 'corner2': [46000,1000]})
block_ent_h = r_bl['id'] if ok_bl and r_bl else None
if block_ent_h:
    test('create_block', 'create_block', {'name': 'TestBlock', 'handles': [block_ent_h], 'base_point': [45500,500]})
else:
    print('  [SKIP] create_block - could not create source entity')
test('list_blocks', 'list_blocks')
test('insert_block', 'insert_block', {'name': 'TestBlock', 'position': [20000, 15000]})

# === VIEW (2) ===
print('\n--- VIEW ---')
test('zoom_extents', 'zoom_extents')
test('zoom_window', 'zoom_window', {'min': [0, 0], 'max': [30000, 20000]})

# === BULK (2 new) ===
print('\n--- BULK ---')
test('bulk_create', 'bulk_create', {'entities': [
    {'type': 'line', 'start': [35000, 0], 'end': [40000, 0]},
    {'type': 'circle', 'center': [37500, 2500], 'radius': 500}
]})
# bulk_erase needs handles or filter
ok_be, r_be, _ = send('create_line', {'start': [60000,0], 'end': [61000,0], 'layer': 'TestLayer'})
be_h = r_be['id'] if ok_be and r_be else None
if be_h:
    test('bulk_erase', 'bulk_erase', {'handles': [be_h], '__confirm': True})
else:
    print('  [SKIP] bulk_erase - could not create test entity')

# === INTERSECTIONS (1 new) ===
print('\n--- INTERSECTIONS ---')
# Create two crossing lines for intersection test
send('create_line', {'start': [40000,0], 'end': [40000,5000]})
ok1, r1 = send('create_line', {'start': [38000,2500], 'end': [42000,2500]})[0:2], send('create_line', {'start': [38000,2500], 'end': [42000,2500]})
test('find_intersections', 'find_intersections', {'handle1': line_h, 'handle2': line_h})

# === SAVE ===
print('\n--- SAVE ---')
test('drawing_save', 'drawing_save', {'path': 'C:/Users/haris/Desktop/mcp_67_tools_test.dwg'})
send('zoom_extents')

# === FINAL SUMMARY ===
print('\n' + '=' * 60)
passed = sum(1 for v in results.values() if v)
total = len(results)
failed = [k for k, v in results.items() if not v]
print(f'  RESULTS: {passed}/{total} passed')
if failed:
    print(f'  FAILED ({len(failed)}): {", ".join(failed)}')
else:
    print('  ALL TOOLS PASSED!')
print('=' * 60)
