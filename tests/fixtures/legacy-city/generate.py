import ipaddress,struct,gzip,json
from pathlib import Path
root=Path(__file__).resolve().parent
def make(edition,records,name):
 nodes=[[None,None]];data=bytearray(b'*');offsets={}
 for ip,lat,lon in records:
  if (lat,lon) not in offsets:
   offsets[lat,lon]=len(data)
   data.extend(bytes([77])+b'XX\0Fixture\0\0'+int(round((lat+180)*10000)).to_bytes(3,'little')+int(round((lon+180)*10000)).to_bytes(3,'little'))
  bits=''.join(f'{b:08b}' for b in ipaddress.ip_address(ip).packed)
  node=0
  for bit in bits[:-1]:
   idx=int(bit)
   if nodes[node][idx] is None:nodes[node][idx]=len(nodes);nodes.append([None,None])
   node=nodes[node][idx]
  nodes[node][int(bits[-1])]=('data',offsets[lat,lon])
 size=len(nodes);out=bytearray()
 for node in nodes:
  for child in node:
   v=size if child is None else size+child[1] if isinstance(child,tuple) else child
   out.extend(v.to_bytes(3,'little'))
 out+=data+b'GeoLite2 City 20261002'+b'\xff\xff\xff'+bytes([edition])+size.to_bytes(3,'little')
 (root/name).write_bytes(out)
 with (root/(name+'.gz')).open('wb') as f:
  with gzip.GzipFile(filename='',fileobj=f,mode='wb',mtime=0) as z:z.write(out)
make(2,[('81.2.69.160',51.5074,-0.1278)],'GeoIPCity.dat')
make(30,[('2001:218::',35.6895,139.6917),('::ffff:81.2.69.160',51.5074,-0.1278)],'GeoIPCityv6.dat')
