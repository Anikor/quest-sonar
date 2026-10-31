# Экспорт YOLOv8n, обученной на Open Images V7 (601 класс, есть "Door"), в ONNX 416x416 для Unity Sentis.
# Лёгкая модель (8.7 GFLOPs на 640, ~3.7 на 416) вместо doors.pt (28 GFLOPs): меньше нагрузка на GPU шлема.
# Запуск: python export_oiv7.py
import os, shutil
os.chdir(os.path.dirname(os.path.abspath(__file__)))
from ultralytics import YOLO

m = YOLO('yolov8n-oiv7.pt')   # скачивается автоматически с github.com/ultralytics/assets
names = m.names
door = [i for i, n in names.items() if n.lower() in ('door', 'doors')]
handle = [i for i, n in names.items() if 'door' in n.lower()]
print('classes:', len(names), 'Door index:', door, 'all door-like:', [(i, names[i]) for i in handle])

if os.path.exists('doors.png'):
    r = m.predict('doors.png', conf=0.2, classes=door, verbose=False)[0]
    print('doors.png detections:', [(names[int(c)], round(float(s), 2), [round(float(v)) for v in b])
                                    for c, s, b in zip(r.boxes.cls, r.boxes.conf, r.boxes.xyxy)])

p = m.export(format='onnx', opset=15, imgsz=416, simplify=True, dynamic=False)
print('exported:', p)
import onnx
mo = onnx.load(p)
print('inputs:', [(i.name, [d.dim_value for d in i.type.tensor_type.shape.dim]) for i in mo.graph.input])
print('outputs:', [(o.name, [d.dim_value for d in o.type.tensor_type.shape.dim]) for o in mo.graph.output])

dst_dir = r'C:\Users\Student 06\Desktop\VR ochiki\Assets\Vision\Resources'
os.makedirs(dst_dir, exist_ok=True)
shutil.copyfile(p, os.path.join(dst_dir, 'doors-oiv7.onnx'))
# индекс класса «дверь» рядом с моделью: DoorVision читает Resources/doors-oiv7-door-class.txt
with open(os.path.join(dst_dir, 'doors-oiv7-door-class.txt'), 'w') as f:
    f.write(str(door[0] if door else -1))
print('copied to', os.path.join(dst_dir, 'doors-oiv7.onnx'))
print('DOOR_CLASS_INDEX =', door[0] if door else -1)
