# Экспорт детектора дверей YOLOv8 (doors.pt) в ONNX для Unity Sentis и проверка на примере.
# Запуск: python export_doors.py   (из папки с doors.pt)
import os, sys
os.chdir(os.path.dirname(os.path.abspath(__file__)))
from ultralytics import YOLO

m = YOLO('doors.pt')
print('classes:', m.names, 'task:', m.task)

r = m.predict('doors.png', conf=0.25, verbose=False)[0]
print('doors.png detections:', [(m.names[int(c)], round(float(s), 2), [round(float(v)) for v in b])
                                for c, s, b in zip(r.boxes.cls, r.boxes.conf, r.boxes.xyxy)])

p = m.export(format='onnx', opset=15, imgsz=640, simplify=True, dynamic=False)
print('exported:', p)

import onnx
mo = onnx.load(p)
print('inputs:', [(i.name, [d.dim_value for d in i.type.tensor_type.shape.dim]) for i in mo.graph.input])
print('outputs:', [(o.name, [d.dim_value for d in o.type.tensor_type.shape.dim]) for o in mo.graph.output])

# копия в проект Unity: Assets/Vision/Resources/doors.onnx (Sentis импортирует как ModelAsset)
dst_dir = r'C:\Users\Student 06\Desktop\VR ochiki\Assets\Vision\Resources'
os.makedirs(dst_dir, exist_ok=True)
import shutil
shutil.copyfile(p, os.path.join(dst_dir, 'doors.onnx'))
print('copied to', os.path.join(dst_dir, 'doors.onnx'))
