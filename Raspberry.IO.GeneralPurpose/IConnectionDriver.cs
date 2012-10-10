namespace Raspberry.IO.GeneralPurpose
{
    public interface IConnectionDriver
    {
        void Write(ProcessorPin pin, bool value);
        bool Read(ProcessorPin pin);
        void Export(PinConfiguration pin);
        void Unexport(PinConfiguration pin);
    }
}