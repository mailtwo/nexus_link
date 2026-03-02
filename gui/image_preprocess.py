import numpy as np
import cv2
from PIL import Image
# Load original
orig = Image.open('./images/world_map.png').convert('RGBA')
arr_orig = np.array(orig)
rgb = arr_orig[...,:3]
bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)

# Parameters chosen
canny1, canny2 = 40, 120
edge_len_thr = 0
dilate_size = 5
dilate_iter = 1

edges = cv2.Canny(gray, canny1, canny2)
num_labels, labels, stats, centroids = cv2.connectedComponentsWithStats((edges>0).astype(np.uint8), connectivity=8)
keep_edges = np.zeros_like(edges, dtype=np.uint8)
for i in range(1, num_labels):
    if stats[i, cv2.CC_STAT_AREA] >= edge_len_thr:
        keep_edges[labels==i]=255

kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (dilate_size,dilate_size))
mask = cv2.dilate(keep_edges, kernel, iterations=dilate_iter)

out = arr_orig.copy()
out[mask==0,:3]=0
out[mask==0,3]=255
out_img = Image.fromarray(out, 'RGBA')
out_path = './world_map_clean.png'
out_img.save(out_path, optimize=True)
print (out_path)
