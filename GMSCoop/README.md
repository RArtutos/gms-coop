# GMS Co-op

Mod de **multijugador cooperativo** para Gold Mining Simulator (Steam 451340), para jugar la misma partida con amigos en tiempo real, por Steam, por internet con Cloudflare o por red local (LAN).

Versión del mod 0.13.0 (protocolo 12). Todos los jugadores necesitan **exactamente la misma versión del mod**.

## Instalar y jugar: GMS Co-op.exe

Todo se hace desde **`GMS Co-op.exe`**: instala el mod, crea la partida o te une a una, y abre el juego con todo listo. Nadie tiene que editar archivos ni escribir IPs en una red local.

1. Abre **`GMS Co-op.exe`**. Windows puede avisar con "Windows protegió tu PC", porque no está firmado: pulsa **Más información** y luego **Ejecutar de todas formas**.
2. Si Windows pregunta por el firewall, pulsa **Permitir**: así ves las partidas de tu red.
3. El launcher busca el juego en tus bibliotecas de Steam e **instala o actualiza el mod solo** (BepInEx 5 + `GMSCoop.dll`). Si no lo encuentra: **Ajustes → Buscar...** y elige `GoldMiningSimulator.exe`.

Las imágenes del launcher salen de la caché de Steam de tu propio PC: el launcher no trae imágenes del juego.

### Crear partida (host)

1. **Crear partida** → elige tu partida guardada (o "Elegir en el juego").
2. Elige cómo se conectan tus amigos:
   - **Steam**: amigos con su propia cuenta de Steam. Invítalos con Shift+Tab, o pásales el **código de partida** (un número largo). Aparece arriba a la izquierda en el juego y se copia solo al portapapeles.
   - **Cloudflare**: funciona con **cualquier internet**, sin abrir puertos ni configurar el router (también con CGNAT). El launcher abre un túnel gratis de Cloudflare y te da un código **`CF-...`**, ya copiado. La primera vez descarga `cloudflared.exe` de Cloudflare (unos 50 MB) en `%APPDATA%\GMSCoop`. **No cierres GMS Co-op mientras juegan.** Los túneles rápidos son el servicio gratis de pruebas de Cloudflare: no tienen garantía de funcionamiento.
   - **Red local / VPN**: misma casa, o misma VPN (Tailscale, ZeroTier, Radmin). En la misma red tus amigos te encuentran solos.
3. Pulsa **Crear partida**: el juego se abre, carga la partida y la abre a tus amigos.

### Unirse

- **Pega el código** que te pasó tu amigo y pulsa **Unirse**:
  - número de Steam (código de partida de Steam);
  - `CF-...` (o la dirección `https://...trycloudflare.com`), para partidas de Cloudflare. No necesitas cloudflared; **no cierres GMS Co-op mientras juegas**;
  - una IP o `IP:puerto`, por ejemplo la IP de VPN del host.
- **En la misma red**: deja el código vacío y pulsa Unirse, o haz clic en la partida de la lista "Partidas en tu red". Cada partida muestra los jugadores, el ping y si la versión coincide (verde) o no (rojo).
- **Invitación de Steam**: acéptala en Steam (Shift+Tab) y listo.

### Conexión directa automática (sin abrir puertos)

En partidas por Cloudflare o por una IP de internet, el juego intenta además **conectarse directo** con el host por UDP (perforación de NAT con servidores STUN públicos; ni servidor propio ni abrir puertos). Si lo logra, el recuadro [Co-op] dice **Directo** junto al jugador; si no, sigue por Cloudflare sin cortar nada y lo reintenta más tarde. Si la conexión directa se cae, el juego vuelve solo al túnel en unos 3 s, sin perder ni repetir datos. La conexión directa va cifrada (AES-128) y autenticada (HMAC-SHA256) con una clave que se pasa por el túnel.

Por el túnel, el juego abre varias conexiones con el host (mundo, juego y dos para las posiciones), así una descarga grande o un paquete perdido no frenan el movimiento de los demás.

El launcher abre el túnel por QUIC y, si tu red lo bloquea, por HTTP/2, y te dice por qué servidor de Cloudflare pasa (por ejemplo Querétaro) y el ping.

### Dos PCs con la misma cuenta de Steam

Steam no conecta una cuenta consigo misma. Usa **Red local** (o Cloudflare) y abre Steam en **modo sin conexión** en uno de los dos PCs.

### Qué hace el launcher y qué no

- Solo escribe dentro de la carpeta del juego (más sus ajustes y cloudflared en `%APPDATA%\GMSCoop`). **Tus partidas guardadas no se tocan.**
- Si ya tienes BepInEx 5 con otros mods, lo deja como está y solo añade o actualiza `GMSCoop.dll`. Con BepInEx 6 no cambia nada y te avisa.
- Crea `steam_appid.txt` junto al juego, para poder abrirlo directamente con las opciones de la partida.
- **Ajustes**: buscar el juego, reinstalar, desinstalar (BepInEx solo se quita si lo puso el launcher y no hay otros mods), gráficos bajos, abrir los registros.

Todos los jugadores necesitan la **misma versión** de GMS Co-op (se ve en el launcher), la misma versión del juego y los mismos DLC que use la partida del host. Al abrir el juego aparece el recuadro "[Co-op]" arriba a la izquierda.

### A mano (sin launcher)

1. Descarga **BepInEx 5.4.x para Windows x64** (no la versión 6). Descomprímelo en la carpeta del juego, junto a `GoldMiningSimulator.exe`.
2. Abre el juego una vez y ciérralo, para que BepInEx cree sus carpetas.
3. Copia `GMSCoop.dll` a `BepInEx\plugins\`.
4. En el juego: host **F8** (Steam) o **F9** (LAN); los demás aceptan la invitación de Steam, o pulsan **F10** para buscar la partida en su red. Para otra IP, ponla en `BepInEx\config\gmscoop.multiplayer.cfg` (`HostIp`).

### Teclas

| Tecla | Qué hace |
|---|---|
| **F8** | Crear partida por Steam (host) |
| **F9** | Crear partida por LAN (host) |
| **F10** | Unirse por LAN: busca la partida en tu red (o usa `HostIp` de la configuración, si pusiste una) |
| **F7** | Salir de la sesión co-op |
| **Ctrl+F6** | Host: lista de jugadores para sacar a uno (pulsa su número; Esc o Ctrl+F6 cancela) |
| **Ctrl+F12** | Herramienta de diagnóstico: escribe un inventario de modelos en el log |
| **Clic de la rueda del mouse** | Marcar un punto: todos ven un marcador con tu nombre, tu color y la distancia (dura 12 s, máximo 3 por jugador) |
| **Ctrl+Shift+F12** | (Host) Juntar los logs de todos los jugadores en `BepInEx
eports` (lo hace solo el botón "Enviar reporte" del launcher) |

F8, F9 y F10 solo funcionan fuera de una sesión; F7, solo dentro de una.

Mientras se muestra una pregunta del mod (lista para sacar jugadores, "[G] / [M]" cuando se cae el host, "[U] / [N]" con una versión más nueva del mundo), el juego no recibe teclas: así los números, G, M, U y N no activan herramientas ni acciones del juego. Al contestar, todo vuelve a la normalidad.

El recuadro de estado (arriba a la izquierda) muestra el último aviso del mod y, durante una sesión, la lista de jugadores:

- **El host** ve a todos los jugadores conectados. **Un cliente** ve solo al host: los clientes no se conectan entre sí, todo pasa por el host.
- **Estado** de cada jugador:
  - **jugando**: todo bien;
  - **recibiendo el mundo**: se está uniendo (solo lo ve el host);
  - **sin datos hace N s**: no llega nada de ese jugador. Si sigue así 30 s, se le desconecta.
- **Calidad de la conexión**:
  - **ms ±**: el tiempo de ida y vuelta (ping) y cuánto varía;
  - **pérdida**: el % de paquetes perdidos ("..." mientras se mide);
  - **colores**: verde = bien, amarillo = regular, rojo = mal.

### Opciones de línea de comandos

El launcher las pasa solas; sirven también para atajos o pruebas.

- `--coop-host`: crea una partida LAN 5 s después de arrancar.
- `--coop-steam-host`: crea una partida de Steam en cuanto Steam está listo.
- `--coop-join <ip[:puerto]>`: se une a esa dirección. `--coop-join auto`: se une a la primera partida que encuentre en la red.
- `+connect_lobby <código>`: se une a la partida de Steam con ese código.
- `--coop-load <partida>`: carga esa partida guardada desde el menú principal.
- `--coop-lowgfx`: gráficos mínimos y 30 FPS. Sirve para abrir dos copias del juego en un mismo PC.
- `--coop-character <id>`: el personaje con el que te ven los demás (`worker`, `q_mannequin`, `kk_barbarian`, `kk_knight`, `kk_mage`, `kk_rogue`, `kk_rogue_hooded`).
- `--coop-probe`, `--coop-test`: solo para pruebas automáticas (ver docs/ARCHITECTURE.md).

## Qué se sincroniza

- **Jugadores**:
  - un personaje con animación de caminar y correr;
  - el nombre encima de cada jugador.
- **Terreno**:
  - excavaciones, alturas y texturas;
  - el oro bajo tierra;
  - los puntos de perforación;
  - el reinicio de parcelas.
- **Economía compartida**: el dinero, el oro, los diamantes y la magnetita cuentan una sola vez, aunque dos jugadores gasten a la vez. También se comparten:
  - préstamos y estadísticas;
  - la hora del día, dormir y el fin de temporada;
  - los trabajadores: contratar, sueldos y despidos.
- **Máquinas y vehículos**:
  - quién conduce;
  - el movimiento suave del vehículo;
  - brazos, ruedas, motor y luces;
  - combustible y desgaste;
  - remolques: enganchar y soltar;
  - transporte o teletransporte por depósito y laptop;
  - el remolque de luces;
  - la carga de la caja de la camioneta;
  - pasajeros.
- **Objetos**:
  - agarrar, lanzar y soltar;
  - poner objetos en soportes;
  - apilar cubetas;
  - cuerdas, ganchos y pistolas de combustible;
  - objetos que crea el juego, como los lingotes de la fragua;
  - objetos destruidos.
- **Compras y ventas**:
  - tienda, vehículos y remolques;
  - parcelas y pestañas del laptop;
  - alquileres: comprar, devolver y la devolución diaria automática;
  - ventas;
  - reinicio de parcela.
- **Procesamiento**:
  - lavadoras (wash plants), cintas y elevadores;
  - tolvas y contenedores;
  - terrones de tierra.
- **Mundo**:
  - interruptores, palancas y puertas;
  - viajes rápidos desbloqueados, notas, puentes, cofres y calabazas;
  - cofres de temporada (Pascua, Navidad, Halloween): si uno lo abre, se ve abierto en todos los PCs y el premio se da una sola vez;
  - el perro de la base: está despierto mientras cualquier jugador esté cerca, y todos ven y oyen cuando alguien lo acaricia, le da de comer o ladra;
  - la fragua;
  - explosiones;
  - pintura;
  - tanques y pedidos de combustible;
  - rayos (el mismo daño en todos los PCs);
  - osos (solo los decide el host).
- **Sonidos de los demás**: escuchas lo que hacen los otros jugadores desde donde están, más bajo si están lejos y con dirección: la pala al excavar y tirar tierra, la batea, las herramientas (martillo, llave, taladro, desarmador, barreta), los pasos y demás sonidos de su personaje. Se usan los sonidos del propio juego, así que respetan tu volumen. El motor de los vehículos que maneja otro ya sonaba según sus revoluciones.
- **Tutorial y misiones**: todos van en el mismo paso.
- **Unirse a mitad de partida**: lo que pase mientras el mundo se envía y se carga se pone al día al terminar la carga. Por ejemplo, máquinas movidas, compras, excavaciones e interruptores.

## Guardar y salir

- **La partida es del host.** El avance de todos se guarda en su partida.
- **El host guarda solo** durante la sesión: cada 5 minutos, cuando sale un jugador y al pulsar F7 (configurable en `BepInEx\config\gmscoop.multiplayer.cfg`, sección `Saves`). El recuadro dice "Guardado hace X min".
- **Cada cliente guarda una copia de respaldo** del mundo compartido en su propio slot `Coop_Respaldo_<host>_<mundo>`: cada 5 minutos y al terminar la sesión. Nunca se escribe en tus partidas personales.
- Mientras juegas en la partida de otro, cualquier guardado va al slot `Coop_Host`, nunca a tus slots. Tu autoguardado se omite durante la sesión.
- **Si el host se desconecta de golpe** (se cae su internet o su juego), en tu pantalla sale: **[G] Guardar y seguir solo / [M] Volver al menú** (el juego espera tu elección). Con G sigues jugando en ese mundo; queda en un slot nuevo `Coop_Respaldo_<host>_solo_<fecha>`, que los respaldos automáticos nunca sobrescriben. Después puedes abrirlo como host desde el launcher y tus amigos vuelven a entrar.
- **El mundo co-op tiene identidad**: cada copia (la del host, los respaldos) sabe qué mundo es y qué tan nueva es (revisión). Si al unirte tienes una copia más nueva que la del host (porque seguiste jugando solo), el host ve: "<nombre> tiene una versión más nueva de este mundo... [U] Usar la suya / [N] Seguir con la mía". Con U, tu copia sube al host, el host guarda la suya aparte (`<slot>_antes_<fecha>`), la carga y se la manda a todos.
- El launcher muestra en cada partida si es "mundo co-op" o "respaldo co-op", con su revisión y quién la guardó al último. Si eliges una partida y hay una copia más nueva del mismo mundo, te ofrece abrir esa.
- **Al salir** (F7, el host se va, o se pierde la conexión), vuelves a tu propia partida (su último guardado). Si te uniste desde el menú principal, vuelves al menú. "Continuar" vuelve a cargar tu partida, no la del host.
- **Si el host carga otra partida** (QuickLoad, menú Cargar o partida nueva), los demás reciben el mundo nuevo automáticamente.
- **Un cliente no puede cargar** la partida compartida (QuickLoad). Si carga una de sus propias partidas, sale de la sesión.

## Español completo

El juego trae español, pero unas 360 líneas salen en inglés. El mod las completa al cargar el juego (solo las que faltan; las demás traducciones y los otros idiomas no cambian). Elige "Español" en Opciones → Idioma, como siempre.

## Actualizaciones

El launcher busca solo una versión nueva al abrirse (en los releases de GitHub del proyecto). Si hay una, muestra "Hay versión nueva X — Actualizar": la descarga, revisa que el archivo esté completo y se reinicia. Todos en una partida necesitan la misma versión; si alguien tiene otra, el juego dice "Versión distinta: abre GMS Co-op para actualizar". Cómo publicar una versión: `release\publish-notes.md`.

## Límites conocidos

- Lo que no habías guardado antes de unirte se pierde, porque el juego carga el mundo del host. Al terminar vuelves a tu último guardado.
- Si se corta la conexión mientras recibes el mundo, al volver a unirte se envía otra vez desde el principio.
- Lo que mueven los demás se ve con un retraso de 0.1 a 0.3 s. Con una conexión mala (Wi-Fi saturado), el retraso es mayor.
- Lo que empuja o arrastra la máquina de otro jugador (también con una cuerda enganchada a su remolque) lo mueve el PC de ese jugador. Si te acercas a pie a esos objetos, no los "tomas": los sigues viendo moverse como en su PC.
- Cuando el host pausa el juego, su física y el procesamiento que él simula se congelan, porque el juego detiene el tiempo. El reloj compartido sigue.
- El nivel de los contenedores que vacía el host se actualiza en pasos de 0.1 s.
- Para que la pintura del Workshop o la pintura propia se vea, el archivo tiene que estar en cada PC.
- El estado de los trabajadores (enfermedad, asignación) no se sincroniza. Los ataques de osos solo ocurren en el mundo del host.
- Si alguien conduce una máquina alquilada que no se pudo pagar, esa máquina no se devuelve hasta otro día, igual que la máquina en la que va sentado el host.
- Si dos jugadores crean el mismo objeto en el mismo sitio y en el mismo segundo (mismo tipo, a menos de 0.5 m, en menos de 3 s), se unen en uno solo.
- En el host, el surtidor puede rechazar una pistola de combustible conectada por otro jugador con "TOO_FAR". Pasa si la posición del vehículo se ve muy retrasada en el host.
- Si dos jugadores venden, funden o gastan el mismo oro (o magnetita) en el mismo instante, cuenta la primera operación que llega al host. La otra se anula: el oro y el dinero vuelven y el juego te avisa.
- Si dos jugadores compran con dinero en el mismo instante y entre los dos gastan más de lo que hay, el dinero queda en negativo. Es una deuda, como cuando el alquiler no alcanza. Nunca aparece dinero de la nada.
- Las plantas de lavado y los procesadores de una zona los procesa el host si tiene la zona cargada. Si no, los procesa el PC de un jugador que sí la tenga. Cuando cambia quién la tiene cargada, se paran unos 3 s.
- Lo que se cava en una zona que el host nunca visita no queda en el archivo de guardado del host, aunque los que se unen sí lo reciben. El juego guarda esas zonas en un formato que el mod no puede escribir sin cargarlas.

## Problemas

- **No aparece el recuadro "[Co-op]"**: abre GMS Co-op.exe → Ajustes → **Reinstalar el mod**. Algunos antivirus borran `winhttp.dll` (el archivo con el que BepInEx arranca): si pasa, añade la carpeta del juego a sus excepciones.
- **"versión del mod distinta"**: todos necesitan el mismo `GMSCoop.dll`. Que todos usen el mismo GMS Co-op.exe.
- **"el juego rechazó el mundo del host"**: tienes otra versión del juego o te faltan DLC.
- **No conecta por LAN**:
  - revisa el firewall: el host tiene que permitir el juego (puerto TCP 27777) y el aviso de partidas (UDP 27778);
  - si la partida no aparece en la lista, pega la IP del host en "Código de partida o IP".
- **No conecta por internet con una IP**: el router del host no deja entrar si no abrió el puerto TCP 27777. Usa Steam o Cloudflare, o una VPN.
- **Cloudflare no arranca**: revisa tu internet y el antivirus (puede bloquear `cloudflared.exe` en `%APPDATA%\GMSCoop`). Si sigue fallando, usa Steam.
- **El log** está en `BepInEx\LogOutput.log`. Envíalo junto con el del otro jugador si reportas un error. El mod escribe poco: un resumen por minuto de cada sistema y los sucesos importantes. Los sucesos rutinarios (objetos creados, puestos en soportes...) solo se escriben las primeras 20 veces por sesión; después solo se cuentan en el resumen. Una sesión de varias horas no llena el disco.

## Compilar (desarrolladores)

Requiere el .NET SDK 8.

```
dotnet build -c Release -p:GameManaged="<carpeta del juego>\GoldMiningSimulator_Data\Managed"
```

Si `GameDir` apunta a la instalación del juego, la DLL se copia sola a `BepInEx\plugins`. La arquitectura para mantenedores está en `docs/ARCHITECTURE.md`.


## Nombre, marcadores y reportes

- **Tu nombre**: el launcher lo pregunta la primera vez (y se cambia en Ajustes). Se ve arriba de tu muñeco, en tus marcadores y en la lista del recuadro, con un color propio por jugador.
- **Muñecos**: los demás jugadores se ven con el modelo de trabajador del propio juego (el conductor de los vehículos), animado al caminar, correr, agacharse, cargar y usar herramientas. Si el modelo no se encuentra, se usa un muñeco simple.
- **Enviar reporte** (launcher, Ajustes, o clic en un mensaje de error): hace un `.zip` en el Escritorio con los registros del juego y del launcher. Si creaste la partida y está abierta, junta también los registros de tus amigos.

## Personajes

En el launcher, **Ajustes → Tu personaje** (y la primera vez que lo abres) eliges cómo te ven tus amigos:

- **Trabajador del juego** (el de siempre): el modelo de trabajador que trae el propio juego.
- **Maniquí** (Quaternius) y **Bárbaro, Caballero, Mago, Pícaro, Pícaro encapuchado** (KayKit).

Todos caminan, corren, se agachan, se sientan al manejar, llevan en la mano derecha la herramienta que tienes (pala, taladro, etc.) y ponen los brazos al frente cuando cargas algo. El launcher pasa tu elección al juego con `--coop-character <id>`, y el mod se la manda a los demás junto con tu nombre. Si a un amigo le falta el archivo de un personaje, te ve como el trabajador del juego.

Los personajes son archivos glTF en `BepInEx\plugins\GMSCoop_avatars\`; el mod los carga solo, en segundo plano.

## Créditos

| Qué | Autor | Licencia |
|---|---|---|
| [KayKit Character Pack: Adventurers](https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0) (personajes y animaciones) | Kay Lousberg ([kaylousberg.com](https://kaylousberg.com)) | CC0 1.0 |
| [Universal Animation Library](https://opengameart.org/content/universal-animation-library) (maniquí y animaciones) | Quaternius ([quaternius.com](https://quaternius.com)) | CC0 1.0 |
| [BepInEx](https://github.com/BepInEx/BepInEx) | BepInEx team | LGPL-2.1 |
| [HarmonyX](https://github.com/BepInEx/HarmonyX) | BepInEx team / Andreas Pardeike | MIT |
| [cloudflared](https://github.com/cloudflare/cloudflared) | Cloudflare | Apache-2.0 |

Detalle de los archivos de personajes: `avatars/LICENSES.md`. Gold Mining Simulator y su modelo de trabajador pertenecen a sus creadores; este mod no está afiliado con ellos.
