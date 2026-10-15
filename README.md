# Lazo

Aplicación para enviar archivos directamente entre equipos Windows 10 y 11 y Mac con Apple Silicon (macOS 14 o posterior) de una misma subred privada. Todo el tráfico va cifrado y cada equipo demuestra quién es. Funciona en segundo plano desde la bandeja y se abre con **doble Alt izquierdo** o **Ctrl+Alt+L**. La ventana emerge desde la base de la pantalla donde está el cursor al abrir y queda centrada horizontalmente. La alerta de recepción aparece abajo a la derecha, sin robar el foco, y muestra un previo si el archivo es una imagen. Al terminar, ofrece **Mostrar en carpeta** y **Abrir**, y permanece visible hasta elegir una opción o cerrar.

Tu ícono aparece en la cabecera de Minimal y Standard y abre tu perfil al pulsarlo. La foto se muestra ampliada en Ajustes y se actualiza inmediatamente al cambiarla. El engranaje de la esquina superior derecha abre los ajustes y guarda la elección. El panel permanece abierto mientras se cambian las opciones. La ventana se arrastra; Escape o un clic fuera la cierra. El nombre que ven los demás es el del usuario, y se puede cambiar en ajustes. Ahí también se elige si Lazo abre con Windows, si busca actualizaciones en GitHub (`maxhine/lazo`) y se cambia o quita la foto de perfil. La imagen se recorta al centro, se reduce a 96 × 96 píxeles en escala de grises y se comparte con los equipos de la subred; sin foto se muestran las iniciales. El acercamiento del cursor al borde de la pantalla ya no abre ninguna ventana.

- **Minimal**: al abrir solo se ve la barra de búsqueda, con círculos de iniciales encima para los equipos conectados. No hay textos. Al escribir, la ventana se expande y muestra resultados; las iniciales de cada fila envían el archivo.
- **Standard**: al abrir se ven los equipos como íconos grandes, en una ventana ajustada a cuántos hay. Al pulsar uno se abre el diálogo para elegir el archivo. También se puede arrastrar un archivo desde el Explorador hasta el ícono: el envío empieza al soltarlo.
- **Apariencia**: Vino (predeterminada en instalaciones nuevas), Plano, Vidrio, Oscuro (grises y negros), Cálido, Meet (inspirado en las llamadas de Google Meet: casi negro, controles gris oscuro, selección azul claro, anillo ámbar y botón de cerrar rojo) e Imagen (usa la foto de inicio desenfocada como fondo de toda la ventana y saca de ella los colores y el acento). Se eligen con miniaturas en Ajustes. Todas comparten el mismo diseño compacto; los equipos se muestran como círculos con un halo al pasar el cursor, sin recuadros. El **acento** se elige aparte. **Liquid Glass** (activado por defecto) vuelve translúcidas, con brillo, las superficies de la ventana sobre el fondo del tema. El **fondo de inicio** se cambia o se restablece desde Ajustes, con clic derecho sobre la tarjeta o con los botones que aparecen al pasar el cursor.
- **Chat**: al estilo de Discord, con conversaciones a la izquierda, mensajes agrupados por autor y separadores por día, archivos como tarjetas y una barra de escritura en píldora (Mayús+Intro para salto de línea). En la cabecera de cada conversación están Compartir pantalla y Zumbido.
- **Compartir pantalla**: desde el chat (abre un selector de pantalla o ventana), desde la tarjeta de inicio o desde el ícono de monitor. Cada persona acepta antes de ver nada y la transmisión se abre en su propia ventana (doble clic o F11 para pantalla completa). Mientras compartes, una barra arriba indica quién mira y permite detener; esa barra no aparece en la transmisión.
- **Dibujar y controlar**: quien comparte pulsa **Dibujar** en esa barra para escribir sobre su pantalla con lápiz, marcador, flecha, recuadro y borrador, en seis colores (Esc o **Terminar** para dejar de dibujar; la papelera borra todo). Quien mira tiene en su ventana los botones de lápiz y de cursor: el primero pide permiso para dibujar y el segundo pide el **control del equipo** (mouse y teclado, como en AnyDesk). Nada ocurre sin que quien comparte lo acepte en el aviso que le aparece, eligiendo el modo: **Ventana activa** (solo la ventana compartida, o la que estaba al frente si se comparte una pantalla; sin tecla Windows, Alt+Tab ni Ctrl+Esc) o **Control total del equipo** (mouse dentro de lo compartido y teclado sin límites); solo una persona controla a la vez, se suelta con el mismo botón y quien comparte lo quita a todos con **Quitar permisos**. Los trazos de todos se ven en la pantalla de quien comparte y en cada visor.

La interfaz usa Bahnschrift con respaldo en Segoe UI. Paneles, chat y cambios de tamaño se animan con fundidos y desplazamientos cortos; respetan la opción de Windows que desactiva las animaciones.

![Lazo Minimal compacto](docs/interfaz-colapsada.png)

## Instalar y compartir

Comparte **`dist/Lazo-Setup-0.5.0.exe`**. Es un solo archivo: contiene Lazo, crea accesos directos, registra la desinstalación en Configuración de Windows y configura dos reglas entrantes limitadas al perfil **Privado** y a la **subred local**. Solicita permisos de administrador. Puede iniciar con Windows si se deja marcada la opción del instalador.

1. Cierra cualquier copia anterior de Lazo desde el icono de la bandeja.
2. Ejecuta `Lazo-Setup-0.5.0.exe` y acepta el aviso de Windows.
3. Abre Lazo desde el menú Inicio. Repite la instalación en el otro equipo.
4. Asegúrate de que ambos equipos estén en una red marcada como **Privada** en Windows.

El instalador no tiene firma digital. Windows puede mostrar **Editor desconocido** o SmartScreen al compartirlo fuera de este equipo. Para distribuirlo sin esa advertencia hace falta firmarlo con un certificado de código de confianza. No se incluye ningún certificado ni clave privada en el proyecto.

También puede generarse un paquete portable con `scripts/package.ps1`, pero el instalador es la opción recomendada.

## Usar

1. Abre la ventana con doble Alt izquierdo o `Ctrl+Alt+L`.
2. En Minimal, escribe parte del nombre. Lazo consulta el índice local de [Everything](https://www.voidtools.com/support/everything/sdk/ipc/), si está abierto en ese equipo. Muestra hasta 24 archivos por búsqueda. También puedes arrastrar un archivo o usar `Ctrl+O`, incluso sin Everything. En Standard, pulsa el equipo para elegir uno o varios archivos, o arrastra varios desde el Explorador hasta su ícono: salen a la vez.
3. En Minimal, pulsa las iniciales del destinatario junto al archivo. En Standard, el archivo elegido o soltado se envía a ese equipo. **Esa acción inicia el envío inmediatamente**.
4. En el receptor aparece una alerta abajo a la derecha. Si llegan varios archivos del mismo equipo, se agrupan y se aceptan todos de una vez. Si el archivo es una imagen, incluye un previo. La solicitud caduca tras 90 segundos.
5. El archivo aceptado se guarda en `Descargas\Lazo`. Se verifica con SHA-256 antes de conservarlo; la alerta se repliega al terminar.

Cerrar la ventana principal la oculta en la bandeja. **Salir** en el menú de la bandeja detiene Lazo.

## Seguridad

Desde la versión 0.5.0 Lazo usa el protocolo **LAZO3** ([especificación](docs/protocolo-seguro.md)): cada equipo tiene una clave propia, cada conexión se cifra (AES-256 con HMAC-SHA256 y claves efímeras ECDH P-256) y cada equipo demuestra quién es con una firma. No es compatible con 0.4.x: hay que actualizar todos los equipos.

- **Sin códigos para usarlo**: la primera vez que dos equipos se conectan, cada uno recuerda la clave del otro (como SSH) y se envía, chatea o comparte sin pasos extra. Quien recibe sigue aceptando cada archivo, pantalla o control.
- Si un equipo conocido aparece con otra clave (por ejemplo, tras reinstalar), Lazo no le envía nada y avisa. Se resuelve con **Olvidar** en **Ajustes › Equipos conocidos**.
- **Verificar (opcional)**: en esa misma lista, **Verificar** muestra a las dos personas un código de 6 cifras; si coincide, nadie se interpuso en la red durante el primer contacto y el equipo queda marcado con ✓.
- Lo recibido lleva la marca de origen de internet (Windows) o de cuarentena (macOS), y los ejecutables piden confirmación antes de abrirse.
- Las actualizaciones solo se instalan si traen una firma válida de la clave de publicación (`scripts/sign-release.sh`).

## macOS

Instala **`dist/Lazo-0.5.0-mac.dmg`**: ábrelo y arrastra Lazo a Aplicaciones. Vive en la barra de menús y se abre con **doble ⌥ izquierdo**, **⌃⌥L** o su icono. Tiene lo mismo que en Windows: Minimal y Standard, temas y Liquid Glass, chat, historial, Descanso Visual, compartir pantalla o una ventana, dibujar y control remoto con los dos modos. La búsqueda usa Spotlight en lugar de Everything.

- La app no tiene firma de Apple (Developer ID). La primera vez, en otro Mac: clic derecho › **Abrir**.
- macOS pide permisos: **Red local** (al abrir), **Grabación de pantalla** (para compartir) y **Accesibilidad** (para que alguien controle el Mac y para el doble ⌥). Están en Ajustes › Permisos de macOS.
- Al controlar un Mac desde Windows, Ctrl se usa como ⌘ (Ctrl+C copia), Alt como ⌥ y la tecla Windows como ⌃.

## Red y requisitos

La pantalla compartida usa el mismo puerto TCP `48352` (protocolo `LAZOS`): captura GDI sincronizada con el refresco de Windows, comparación por bloques de 128 × 64 contra el último fotograma enviado y JPEG solo de lo que cambió, con reenvío nítido cuando una zona se queda quieta. Las fuentes de más de 2560 × 1600 se reducen. Ritmo hasta 60 fps; con la pantalla quieta, el consumo de red es casi nulo. Las ventanas se capturan aunque estén tapadas; si se minimizan, la imagen se congela hasta restaurarlas.

Dibujar y controlar usan la misma conexión en sentido inverso. El visor saluda al empezar y solo si quien comparte responde aparecen sus botones, así que se puede mezclar con versiones anteriores: un visor antiguo sigue viendo la pantalla sin estas opciones y un emisor antiguo ignora el saludo. Los trazos viajan como vectores con coordenadas relativas a lo compartido; en el equipo que comparte se pintan en una capa transparente que no entra en la captura. El control remoto reproduce mouse y teclado con `SendInput`, limitado al área compartida; al quitar el control se sueltan las teclas y botones que hubieran quedado pulsados. Windows no deja que esa entrada llegue a ventanas que corren como administrador (por ejemplo, avisos de UAC) si Lazo no lo está. Este canal va cifrado como el resto; aun así, concede el control solo a quien conoces.

Lazo anuncia su presencia (nombre y huella de su clave) por UDP `48351` y transfiere por TCP `48352`. Los nombres aparecen solo si **ambos equipos tienen Lazo abierto**, están en la misma subred IPv4 privada y el firewall permite la conexión. No usa carpetas compartidas ni permisos SMB. Una red Wi‑Fi con aislamiento entre clientes puede impedir el descubrimiento.

La búsqueda rápida requiere Everything instalado y ejecutándose en el equipo que envía. Lazo no instala Everything ni copia su base de datos; consulta su índice mediante IPC local. Los resultados dependen de las carpetas que Everything tenga indexadas. El receptor no necesita Everything.

Requiere .NET Framework 4.8 o superior. El ejecutable se compiló y probó en Windows 11; la validación entre dos equipos físicos, incluida Windows 10, sigue pendiente.

## Desarrollo y verificación

No requiere SDK de .NET ni paquetes NuGet en el equipo de desarrollo; usa el compilador de .NET Framework.

```powershell
.\scripts\build.ps1 -Release
.\scripts\test-everything.ps1
.\scripts\test-network.ps1
.\scripts\build-installer.ps1
.\scripts\test-installer.ps1
.\scripts\test-screen.ps1 -Mode window
.\scripts\test-control.ps1
```

En un Mac (requiere Swift y `brew install dotnet`):

```sh
./scripts/build-mac.sh        # dist/Lazo-0.5.0-mac.dmg
./scripts/test-interop.sh     # motor de Windows (C#) contra el de Mac: archivo y chat en los dos sentidos
~/Library/Caches/lazo-build/debug/Lazo --self-test
```

`test-everything.ps1` comprueba la consulta y respuesta Unicode contra un servidor IPC simulado. `test-network.ps1` hace una transferencia real a una IP privada del propio equipo y compara el archivo recibido. `test-installer.ps1` comprueba el ejecutable incluido. `test-control.ps1` comprueba, sin mover el mouse real, el saludo, los permisos de dibujar y controlar, los trazos en ambos sentidos y que un visor de una versión anterior solo reciba los paquetes de siempre. La conexión de búsqueda con una instancia activa de Everything aún debe comprobarse en una sesión de escritorio normal: la instancia instalada en este entorno no expone su ventana IPC a la sesión de prueba.

## Alcance de esta versión

- Un archivo por envío, hasta 20 GB; una recepción activa a la vez.
- El tráfico va cifrado y autenticado entre equipos emparejados. El anuncio de presencia por UDP no está firmado: un equipo puede aparecer en la lista con cualquier nombre. Nada llega sin que lo aceptes y, si quieres estar seguro de quién es, verifica el código.
- Doble Alt deja intacto el comportamiento normal de Alt en otras aplicaciones; alguna puede activar su menú tras el primer toque. `Ctrl+Alt+L` queda como alternativa.
- Instaladores para Windows (`.exe`) y para Mac con Apple Silicon (`.dmg`). La actualización automática exige una firma válida.
