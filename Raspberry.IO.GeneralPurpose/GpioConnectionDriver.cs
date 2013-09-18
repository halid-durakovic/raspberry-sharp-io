using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Raspberry.IO.GeneralPurpose
{
    /// <summary>
    /// Represents the default connection driver that uses memory for accesses and files for edge detection.
    /// </summary>
    public class GpioConnectionDriver : IGpioConnectionDriver
    {
        #region Fields

        private readonly IntPtr gpioAddress;
        private const string gpioPath = "/sys/class/gpio";

        #endregion

        #region Instance Management

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryGpioConnectionDriver"/> class.
        /// </summary>
        public GpioConnectionDriver()
        {
            var memoryFile = Interop.open("/dev/mem", Interop.O_RDWR + Interop.O_SYNC);
            try
            {
                gpioAddress = Interop.mmap(IntPtr.Zero, Interop.BCM2835_BLOCK_SIZE, Interop.PROT_READ | Interop.PROT_WRITE, Interop.MAP_SHARED, memoryFile, Interop.BCM2835_GPIO_BASE);
            }
            finally
            {
                Interop.close(memoryFile);
            }

            if (gpioAddress == (IntPtr)Interop.MAP_FAILED)
                throw new InvalidOperationException();
        }

        /// <summary>
        /// Releases unmanaged resources and performs other cleanup operations before the
        /// <see cref="MemoryGpioConnectionDriver"/> is reclaimed by garbage collection.
        /// </summary>
        ~GpioConnectionDriver()
        {
            Interop.munmap(gpioAddress, Interop.BCM2835_BLOCK_SIZE);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Allocates the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <param name="direction">The direction.</param>
        public void Allocate(ProcessorPin pin, PinDirection direction)
        {
            var gpioId = string.Format("gpio{0}", (int)pin);
            if (Directory.Exists(Path.Combine(gpioPath, gpioId)))
            {
                // Reinitialize pin virtual file
                using (var streamWriter = new StreamWriter(Path.Combine(gpioPath, "unexport"), false))
                    streamWriter.Write((int) pin);
            }

            // Export pin for file mode
            using (var streamWriter = new StreamWriter(Path.Combine(gpioPath, "export"), false))
                streamWriter.Write((int)pin);

            // Set the direction on the pin and update the exported list
            SetPinMode(pin, direction == PinDirection.Input ? Interop.BCM2835_GPIO_FSEL_INPT : Interop.BCM2835_GPIO_FSEL_OUTP);

            // Set direction in pin virtual file
            var filePath = Path.Combine(gpioId, "direction");
            using (var streamWriter = new StreamWriter(Path.Combine(gpioPath, filePath), false))
                streamWriter.Write(direction == PinDirection.Input ? "in" : "out");

            if (direction == PinDirection.Input)
                SetPinResistor(pin, PinResistor.None);
        }

        /// <summary>
        /// Sets the pin resistor.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <param name="resistor">The resistor.</param>
        public void SetPinResistor(ProcessorPin pin, PinResistor resistor)
        {
            // Set the pullup/down resistor for a pin
            //
            // The GPIO Pull-up/down Clock Registers control the actuation of internal pull-downs on
            // the respective GPIO pins. These registers must be used in conjunction with the GPPUD
            // register to effect GPIO Pull-up/down changes. The following sequence of events is
            // required:
            // 1. Write to GPPUD to set the required control signal (i.e. Pull-up or Pull-Down or neither
            // to remove the current Pull-up/down)
            // 2. Wait 150 cycles ? this provides the required set-up time for the control signal
            // 3. Write to GPPUDCLK0/1 to clock the control signal into the GPIO pads you wish to
            // modify ? NOTE only the pads which receive a clock will be modified, all others will
            // retain their previous state.
            // 4. Wait 150 cycles ? this provides the required hold time for the control signal
            // 5. Write to GPPUD to remove the control signal
            // 6. Write to GPPUDCLK0/1 to remove the clock
            //
            // RPi has P1-03 and P1-05 with 1k8 pullup resistor

            uint pud;
            switch (resistor)
            {
                case PinResistor.None:
                    pud = Interop.BCM2835_GPIO_PUD_OFF;
                    break;
                case PinResistor.PullDown:
                    pud = Interop.BCM2835_GPIO_PUD_DOWN;
                    break;
                case PinResistor.PullUp:
                    pud = Interop.BCM2835_GPIO_PUD_UP;
                    break;

                default:
                    throw new ArgumentOutOfRangeException("resistor");
            }

            WriteResistor(pud);
            Timers.Timer.Sleep(1);
            SetPinResistorClock(pin, true);
            Timers.Timer.Sleep(1);
            WriteResistor(Interop.BCM2835_GPIO_PUD_OFF);
            SetPinResistorClock(pin, false);
        }

        /// <summary>
        /// Releases the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        public void Release(ProcessorPin pin)
        {
            SetPinMode(pin, Interop.BCM2835_GPIO_FSEL_INPT);

            using (var streamWriter = new StreamWriter(Path.Combine(gpioPath, "unexport"), false))
                streamWriter.Write((int)pin);
        }

        /// <summary>
        /// Modified the status of a pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <param name="value">The pin status.</param>
        public void Write(ProcessorPin pin, bool value)
        {
            int shift;
            var offset = Math.DivRem((int)pin, 32, out shift);

            var pinGroupAddress = gpioAddress + (int)((value ? Interop.BCM2835_GPSET0 : Interop.BCM2835_GPCLR0) + offset);
            SafeWriteUInt32(pinGroupAddress, (uint)1 << shift);
        }

        /// <summary>
        /// Reads the status of the specified pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <returns>
        /// The pin status.
        /// </returns>
        public bool Read(ProcessorPin pin)
        {
            int shift;
            var offset = Math.DivRem((int)pin, 32, out shift);

            var pinGroupAddress = gpioAddress + (int)(Interop.BCM2835_GPLEV0 + offset);
            var value = SafeReadUInt32(pinGroupAddress);

            return (value & (1 << shift)) != 0;
        }

        /// <summary>
        /// Reads the status of the specified pins.
        /// </summary>
        /// <param name="pins">The pins.</param>
        /// <returns>
        /// The pins status.
        /// </returns>
        public ProcessorPins Read(ProcessorPins pins)
        {
            var pinGroupAddress = gpioAddress + (int)(Interop.BCM2835_GPLEV0 + (uint)0 * 4);
            var value = SafeReadUInt32(pinGroupAddress);

            return (ProcessorPins)((uint)pins & value);
        }

        #endregion

        #region Private Methods

        private void SetPinResistorClock(ProcessorPin pin, bool on)
        {
            int shift;
            var offset = Math.DivRem((int)pin, 32, out shift);

            var clockAddress = gpioAddress + (int)(Interop.BCM2835_GPPUDCLK0 + offset);
            SafeWriteUInt32(clockAddress, (uint)(on ? 1 : 0) << shift);
        }

        private void WriteResistor(uint resistor)
        {
            var resistorPin = gpioAddress + (int)Interop.BCM2835_GPPUD;
            SafeWriteUInt32(resistorPin, resistor);
        }

        private void SetPinMode(ProcessorPin pin, uint mode)
        {
            // Function selects are 10 pins per 32 bit word, 3 bits per pin
            var pinModeAddress = gpioAddress + (int)(Interop.BCM2835_GPFSEL0 + 4 * ((int)pin / 10));

            var shift = 3 * ((int)pin % 10);
            var mask = Interop.BCM2835_GPIO_FSEL_MASK << shift;
            var value = mode << shift;

            WriteUInt32Mask(pinModeAddress, value, mask);
        }

        private static void WriteUInt32Mask(IntPtr address, uint value, uint mask)
        {
            var v = SafeReadUInt32(address);
            v = (v & ~mask) | (value & mask);
            SafeWriteUInt32(address, v);
        }

        private static uint SafeReadUInt32(IntPtr address)
        {
            // Make sure we dont return the _last_ read which might get lost
            // if subsequent code changes to a different peripheral
            var ret = ReadUInt32(address);
            ReadUInt32(address);

            return ret;
        }

        private static uint ReadUInt32(IntPtr address)
        {
            unchecked
            {
                return (uint)Marshal.ReadInt32(address);
            }
        }

        private static void SafeWriteUInt32(IntPtr address, uint value)
        {
            // Make sure we don't rely on the first write, which may get
            // lost if the previous access was to a different peripheral.
            WriteUInt32(address, value);
            WriteUInt32(address, value);
        }

        private static void WriteUInt32(IntPtr address, uint value)
        {
            unchecked
            {
                Marshal.WriteInt32(address, (int)value);
            }
        }

        #endregion
    }
}